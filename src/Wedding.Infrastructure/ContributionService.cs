using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wedding.Application;
using Wedding.Domain;

namespace Wedding.Infrastructure;

public sealed class ContributionService(WeddingDbContext db, TrackingTokens tokens)
{
    public async Task<(ContributionReceipt Receipt, bool Created)> CreateAsync(ContributionInput input, string? key, CancellationToken ct)
    {
        var id = Rules.IdempotencyKey(key);
        input = Rules.Normalize(input);
        var hash = Rules.Hash(id.ToString());
        var request = Rules.Fingerprint(input);
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Operation == "contribution.create" && x.KeyHash == hash, ct);
        if (existing is not null) return (await ReplayAsync(existing, request, ct), false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var gift = await db.Gifts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.GiftId && x.Active, ct) ?? throw ApiException.Missing();
        var credential = tokens.Create();
        var contribution = new Contribution { GiftId = gift.Id, GiftTitleSnapshot = gift.Title, AmountCentsSnapshot = gift.AmountCents,
            GuestName = input.GuestName, Email = input.Email, Message = input.Message, TrackingTokenHash = credential.Hash, ProtectedTrackingToken = credential.Protected };
        db.Contributions.Add(contribution);
        db.IdempotencyRecords.Add(new IdempotencyRecord { Operation = "contribution.create", KeyHash = hash, RequestHash = request, ContributionId = contribution.Id });
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (new ContributionReceipt(contribution.Id, credential.Token), true);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            existing = await db.IdempotencyRecords.AsNoTracking().SingleAsync(x => x.Operation == "contribution.create" && x.KeyHash == hash, ct);
            return (await ReplayAsync(existing, request, ct), false);
        }
    }
    private async Task<ContributionReceipt> ReplayAsync(IdempotencyRecord record, string hash, CancellationToken ct)
    {
        if (record.RequestHash != hash) throw ApiException.Conflict();
        var contribution = await db.Contributions.AsNoTracking().SingleAsync(x => x.Id == record.ContributionId, ct);
        return new ContributionReceipt(contribution.Id, tokens.Recover(contribution.ProtectedTrackingToken));
    }
    public async Task<Contribution> AuthorizeAsync(Guid id, string? token, CancellationToken ct)
    {
        var contribution = await db.Contributions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        // Always perform the token hash/comparison, including for a missing contribution.
        var valid = tokens.Verify(token, contribution?.TrackingTokenHash ?? new byte[32]);
        if (contribution is null || !valid) throw ApiException.Missing();
        return contribution;
    }
}
