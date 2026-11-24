using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wedding.Application;
using Wedding.Domain;

namespace Wedding.Infrastructure;

public sealed class CheckoutService(WeddingDbContext db, ContributionService contributions, TrackingTokens tokens,
    IMercadoPagoClient provider, IDataProtectionProvider protection, IOptions<MercadoPagoOptions> options,
    IOptions<FrontendOptions> frontend, FinancialSync financial, ILogger<CheckoutService> logger)
{
    private readonly IDataProtector requestProtector = protection.CreateProtector("Wedding.CheckoutRequest.v1");
    public async Task<CheckoutResult> GetAsync(Guid id, string? token, string? key, CancellationToken ct)
    {
        await contributions.AuthorizeAsync(id, token, ct);
        var clientKey = Rules.IdempotencyKey(key);
        if (!options.Value.CheckoutEnabled) throw ApiException.Unavailable();
        PaymentAttempt attempt;
        var owner = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var contribution = await db.Contributions.FromSqlInterpolated($"SELECT * FROM \"Contributions\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
            if (contribution.Status != "pending") throw ApiException.Conflict();
            var scope = "contribution.checkout:" + id;
            var keyHash = Rules.Hash(clientKey.ToString());
            var fingerprint = Rules.Fingerprint(new { contributionId = id });
            var record = await db.IdempotencyRecords.FindAsync([scope, keyHash], ct);
            if (record is not null && record.RequestHash != fingerprint) throw ApiException.Conflict();
            if (record is null) db.IdempotencyRecords.Add(new IdempotencyRecord { Operation = scope, KeyHash = keyHash, RequestHash = fingerprint, ContributionId = id });
            attempt = (await db.PaymentAttempts.SingleOrDefaultAsync(x => x.ContributionId == id, ct))!;
            if (attempt is not null)
            {
                if (attempt.ManualIntervention || attempt.CreationState == "failed") throw ApiException.Conflict();
                if (attempt.CreationState == "ready" && attempt.CheckoutUrl is { } url && (attempt.CheckoutExpiresAt is null || attempt.CheckoutExpiresAt > DateTimeOffset.UtcNow))
                {
                    await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return new CheckoutResult(url);
                }
                // Once an attempt exists, creation/recovery belongs to the durable worker.
                if (attempt.CreationState == "creating" && attempt.LeaseUntil <= DateTimeOffset.UtcNow)
                {
                    attempt.CreationState = "uncertain"; attempt.NextSyncAt = DateTimeOffset.UtcNow; attempt.Version++;
                }
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                throw ApiException.Unavailable();
            }
            attempt = new PaymentAttempt { ContributionId = id, LeaseOwner = owner, LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(60), NextSyncAt = DateTimeOffset.UtcNow.AddMinutes(1) };
            attempt.ProtectedRequest = requestProtector.Protect(BuildRequest(contribution, attempt.Id));
            db.PaymentAttempts.Add(attempt);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        }
        // Cancellation can stop HTTP work, but the committed attempt and its key remain recoverable after the lease expires.
        return await CreatePersistedAsync(attempt.Id, owner, ct, initialCreation: true);
    }
    private string BuildRequest(Contribution contribution, Guid attemptId)
    {
        var returnUrl = frontend.Value.PublicOrigin.TrimEnd('/') + "/presentes/acompanhar/" + contribution.Id + "#token=" + Uri.EscapeDataString(tokens.Recover(contribution.ProtectedTrackingToken));
        var config = JsonNode.Parse(options.Value.ValidatedConfigJson)?.AsObject() ?? throw ApiException.Unavailable();
        var online = config["online"] as JsonObject ?? new JsonObject();
        online["success_url"] = returnUrl;
        online["pending_url"] = returnUrl;
        online["failure_url"] = returnUrl;
        online["retries"] = new JsonObject { ["allowed"] = false };
        config["online"] = online;
        var money = (contribution.AmountCentsSnapshot / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(new { type = "online", processing_mode = "manual", capture_mode = "automatic_async",
            total_amount = money, external_reference = attemptId.ToString(), description = contribution.GiftTitleSnapshot,
            payer = new { email = contribution.Email }, items = new[] { new { title = contribution.GiftTitleSnapshot, unit_price = money, quantity = 1 } },
            config });
    }
    public async Task<CheckoutResult> CreatePersistedAsync(Guid attemptId, Guid owner, CancellationToken ct, bool initialCreation = false)
    {
        db.ChangeTracker.Clear();
        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId, ct);
        if (attempt.LeaseOwner != owner) throw ApiException.Unavailable();
        if (!options.Value.CheckoutEnabled) throw ApiException.Unavailable();
        if (options.Value.SandboxSingleAttempt && !initialCreation)
        {
            await db.PaymentAttempts.Where(x => x.Id == attemptId && x.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ManualIntervention, true).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.LastErrorCode, "sandbox_creation_retry_disabled"), ct);
            throw ApiException.Conflict();
        }
        if (!options.Value.SandboxSingleAttempt && attempt.CreatedAt.AddMinutes(options.Value.IdempotencyWindowMinutes) <= DateTimeOffset.UtcNow)
        {
            await db.PaymentAttempts.Where(x => x.Id == attemptId && x.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ManualIntervention, true).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LastErrorCode, "idempotency_window_expired"), ct);
            logger.LogError("Payment attempt {AttemptId} requires operational reconciliation", attemptId);
            throw ApiException.Conflict();
        }
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await provider.CreateOrderAsync(requestProtector.Unprotect(attempt.ProtectedRequest), attempt.ProviderIdempotencyKey, budget.Token);
            if (result.CheckoutUrl is null || !Rules.CheckoutUrl(result.CheckoutUrl)) throw new ApiException(502, "invalid_checkout_url", "Resposta inválida do provedor.");
            await financial.ApplyAsync(result, ct);
            db.ChangeTracker.Clear();
            await db.PaymentAttempts.Where(x => x.Id == attemptId && x.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CreationState, "ready").SetProperty(x => x.CheckoutUrl, result.CheckoutUrl)
                .SetProperty(x => x.CheckoutExpiresAt, result.ExpiresAt).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.LeaseOwner, (Guid?)null).SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddMinutes(5))
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
            return new CheckoutResult(result.CheckoutUrl);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or OperationCanceledException)
        {
            // Do not delay an expired HTTP request for another database write. The durable creating lease is recovered by cron.
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            db.ChangeTracker.Clear();
            var definitive = ex is ApiException { Code: "provider_creation_failed" };
            await db.PaymentAttempts.Where(x => x.Id == attemptId && x.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CreationState, definitive ? "failed" : "uncertain").SetProperty(x => x.LastErrorCode, ex is ApiException ? ((ApiException)ex).Code : "provider_unavailable")
                .SetProperty(x => x.ManualIntervention, options.Value.SandboxSingleAttempt && !definitive)
                .SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddMinutes(1)).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.LeaseOwner, (Guid?)null).SetProperty(x => x.Version, x => x.Version + 1), CancellationToken.None);
            if (!definitive) OperationalMetrics.Uncertain.Add(1);
            throw;
        }
    }
}
