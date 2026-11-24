using Microsoft.EntityFrameworkCore;
using Wedding.Application;
using Wedding.Domain;

namespace Wedding.Infrastructure;

public sealed class DurableProcessor(WeddingDbContext db, IMercadoPagoClient provider, FinancialSync financial,
    CheckoutService checkout, IOptions<ProcessingOptions> settings, ILogger<DurableProcessor> logger)
{
    public async Task<bool> ProcessInboxAsync(CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        WebhookInbox? item;
        var owner = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            item = await db.WebhookInbox.FromSqlInterpolated($"SELECT * FROM \"WebhookInbox\" WHERE \"ProcessedAt\" IS NULL AND NOT \"ManualIntervention\" AND \"NextAttemptAt\" <= {now} AND (\"LeaseUntil\" IS NULL OR \"LeaseUntil\" <= {now}) ORDER BY \"NextAttemptAt\", \"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
            if (item is null) return false;
            item.LeaseOwner = owner; item.LeaseUntil = now.AddSeconds(settings.Value.LeaseSeconds); item.Attempts++;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        OperationalMetrics.InboxAge.Record((DateTimeOffset.UtcNow - item.ReceivedAt).TotalSeconds);
        try
        {
            if (item.Topic == "order")
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(10));
                var order = await provider.GetOrderAsync(item.ProviderResourceId, budget.Token);
                await financial.ApplyAsync(order, ct);
            }
            else logger.LogWarning("Ignored authenticated unsupported topic for inbox {InboxId}", item.Id);
            await db.WebhookInbox.Where(x => x.Id == item.Id && x.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.LeaseOwner, (Guid?)null).SetProperty(x => x.LastErrorCode, item.Topic == "order" ? null : "unsupported_topic"), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var exhausted = item.Attempts >= settings.Value.MaxAttempts;
            var code = ex is ApiException api ? api.Code : "processing_failed";
            await db.WebhookInbox.Where(x => x.Id == item.Id && x.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastErrorCode, code).SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(Backoff(item.Attempts)))
                .SetProperty(x => x.ManualIntervention, exhausted).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseOwner, (Guid?)null), ct);
            OperationalMetrics.ReconciliationFailures.Add(1);
            logger.Log(exhausted ? LogLevel.Error : LogLevel.Warning, "Inbox {InboxId} processing failed with {Code}; intervention {Intervention}", item.Id, code, exhausted);
        }
        return true;
    }
    public async Task<bool> ProcessAttemptAsync(Guid? contributionId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        PaymentAttempt? attempt;
        var owner = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-30);
            var targeted = contributionId is not null || from is not null || to is not null;
            // SQL selection and row lease are atomic; no session advisory locks are used.
            var query = db.PaymentAttempts.FromSqlInterpolated($"SELECT a.* FROM \"PaymentAttempts\" a JOIN \"Contributions\" c ON c.\"Id\" = a.\"ContributionId\" WHERE NOT a.\"ManualIntervention\" AND (a.\"NextSyncAt\" IS NULL OR a.\"NextSyncAt\" <= {now}) AND (a.\"LeaseUntil\" IS NULL OR a.\"LeaseUntil\" <= {now}) AND ({targeted} OR c.\"Status\" = 'pending' OR (c.\"Status\" = 'approved' AND c.\"CreatedAt\" >= {cutoff}) OR (c.\"Status\" IN ('refunded', 'charged_back') AND c.\"NetAmountCents\" IS NULL))");
            query = query.Where(x => x.CreationState != "failed");
            if (contributionId is { } id) query = query.Where(x => x.ContributionId == id);
            if (from is { } start) query = query.Where(x => x.CreatedAt >= start);
            if (to is { } end) query = query.Where(x => x.CreatedAt <= end);
            // Lock is acquired separately with SKIP LOCKED, then eligibility is checked again.
            var candidates = await query.OrderBy(x => x.NextSyncAt).Select(x => x.Id).Take(100).ToListAsync(ct);
            attempt = null;
            foreach (var candidate in candidates)
            {
                attempt = await db.PaymentAttempts.FromSqlInterpolated($"SELECT * FROM \"PaymentAttempts\" WHERE \"Id\" = {candidate} AND (\"LeaseUntil\" IS NULL OR \"LeaseUntil\" <= {now}) AND (\"NextSyncAt\" IS NULL OR \"NextSyncAt\" <= {now}) AND NOT \"ManualIntervention\" FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
                if (attempt is not null) break;
            }
            if (attempt is null) return false;
            attempt.LeaseOwner = owner; attempt.LeaseUntil = now.AddSeconds(settings.Value.LeaseSeconds); attempt.SyncAttempts++; attempt.Version++;
            if (attempt.CreationState == "creating") attempt.CreationState = "uncertain";
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        try
        {
            if (attempt.ProviderOrderId is { } externalId)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(10));
                var order = await provider.GetOrderAsync(externalId, budget.Token);
                await financial.ApplyAsync(order, ct);
                if (attempt.CreationState != "ready" && Rules.MapStatus(order.Status, order.StatusDetail, order.RefundedAmountCents) == "pending")
                {
                    if (order.CheckoutUrl is not null)
                        await db.PaymentAttempts.Where(x => x.Id == attempt.Id && x.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.CreationState, "ready").SetProperty(x => x.CheckoutUrl, order.CheckoutUrl).SetProperty(x => x.CheckoutExpiresAt, order.ExpiresAt), ct);
                    else await checkout.CreatePersistedAsync(attempt.Id, owner, ct);
                }
            }
            else await checkout.CreatePersistedAsync(attempt.Id, owner, ct);
            await db.PaymentAttempts.Where(x => x.Id == attempt.Id && (x.LeaseOwner == owner || x.LeaseOwner == null)).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddMinutes(5)).SetProperty(x => x.SyncAttempts, 0)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseOwner, (Guid?)null), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var code = ex is ApiException api ? api.Code : "reconcile_failed";
            var exhausted = attempt.SyncAttempts >= settings.Value.MaxAttempts;
            await db.PaymentAttempts.Where(x => x.Id == attempt.Id && (x.LeaseOwner == owner || x.LeaseOwner == null)).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastErrorCode, code).SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddSeconds(Backoff(attempt.SyncAttempts)))
                .SetProperty(x => x.ManualIntervention, x => x.ManualIntervention || exhausted).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseOwner, (Guid?)null), ct);
            OperationalMetrics.ReconciliationFailures.Add(1);
            logger.Log(exhausted ? LogLevel.Error : LogLevel.Warning, "Attempt {AttemptId} reconciliation failed with {Code}", attempt.Id, code);
        }
        return true;
    }
    private static double Backoff(int attempts) => Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts, 10))) + Random.Shared.Next(0, 10);
}
public sealed class InboxWorker(IServiceScopeFactory scopes, IOptions<ProcessingOptions> options, ILogger<InboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DurableProcessor>();
                for (var i = 0; i < 100 && await processor.ProcessInboxAsync(stoppingToken); i++) { }
                var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
                var outstanding = db.WebhookInbox.Where(x => x.ProcessedAt == null);
                var oldest = await outstanding.Select(x => (DateTimeOffset?)x.ReceivedAt).MinAsync(stoppingToken);
                OperationalMetrics.Snapshot(await outstanding.LongCountAsync(stoppingToken),
                    await db.PaymentAttempts.LongCountAsync(x => x.CreationState == "uncertain", stoppingToken),
                    await outstanding.LongCountAsync(x => x.ManualIntervention, stoppingToken) + await db.PaymentAttempts.LongCountAsync(x => x.ManualIntervention, stoppingToken),
                    oldest is null ? 0 : (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogError("Inbox processing unavailable; retrying after polling interval"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken);
        }
    }
}
