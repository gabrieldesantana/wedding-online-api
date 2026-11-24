using Microsoft.EntityFrameworkCore;
using Wedding.Application;

namespace Wedding.Infrastructure;

public sealed class FinancialSync(WeddingDbContext db, IOptions<MercadoPagoOptions> settings, ILogger<FinancialSync> logger)
{
    public async Task ApplyAsync(ProviderOrder order, CancellationToken ct)
    {
        if (!Guid.TryParse(order.Reference, out var attemptId)) throw Mismatch("reference");
        var link = await db.PaymentAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId, ct);
        if (link is null) throw Mismatch("unknown_reference");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var contribution = await db.Contributions.FromSqlInterpolated($"SELECT * FROM \"Contributions\" WHERE \"Id\" = {link.ContributionId} FOR UPDATE").SingleAsync(ct);
        var attempt = await db.PaymentAttempts.SingleAsync(x => x.Id == attemptId, ct);
        if (order.ApplicationId != settings.Value.ApplicationId || order.ReceiverId != settings.Value.ReceiverId || order.LiveMode != (settings.Value.Environment == "production") || order.Currency != "BRL" || order.AmountCents != contribution.AmountCentsSnapshot || (attempt.ProviderOrderId is not null && attempt.ProviderOrderId != order.Id))
            throw Mismatch("financial_identity");
        var previous = attempt.ProviderUpdatedAt;
        if (previous is not null && order.UpdatedAt < previous) return;
        attempt.ProviderOrderId = order.Id;
        attempt.ProviderStatus = order.Status;
        attempt.ProviderUpdatedAt = order.UpdatedAt;
        attempt.UpdatedAt = DateTimeOffset.UtcNow;
        attempt.Version++;
        var mapped = Rules.MapStatus(order.Status, order.StatusDetail, order.RefundedAmountCents);
        if (mapped is null)
        {
            logger.LogError("Unknown provider status for attempt {AttemptId}", attempt.Id);
            attempt.LastErrorCode = "unknown_provider_status";
        }
        else
        {
            // Equal timestamps may carry different projections: never regress a confirmed state to pending.
            if (!(mapped == "pending" && contribution.Status != "pending") &&
                !(mapped == "approved" && contribution.Status is "refunded" or "charged_back") &&
                !(order.UpdatedAt == previous && contribution.Status is "refunded" or "charged_back" && mapped != contribution.Status)) contribution.Status = mapped;
            contribution.GrossAmountCents = order.AmountCents;
            contribution.FeeAmountCents = order.FeeAmountCents ?? contribution.FeeAmountCents;
            contribution.NetAmountCents = order.NetAmountCents ?? contribution.NetAmountCents;
            contribution.RefundedAmountCents = Math.Max(contribution.RefundedAmountCents, order.RefundedAmountCents);
        }
        contribution.LastProviderSyncAt = DateTimeOffset.UtcNow;
        contribution.UpdatedAt = DateTimeOffset.UtcNow;
        contribution.Version++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    private ApiException Mismatch(string code)
    {
        logger.LogError("Authoritative payment verification failed: {Code}", code);
        OperationalMetrics.Mismatches.Add(1);
        return new ApiException(502, "financial_mismatch", "Resposta financeira incompatível.");
    }
}
