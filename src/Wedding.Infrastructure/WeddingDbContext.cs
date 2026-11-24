using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Wedding.Domain;

namespace Wedding.Infrastructure;

public sealed class CoupleUser : IdentityUser<Guid>
{
    public string Name { get; set; } = "";
}
public sealed class StoredSession
{
    public string Id { get; set; } = "";
    public Guid UserId { get; set; }
    public byte[] Ticket { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class WeddingDbContext(DbContextOptions<WeddingDbContext> options)
    : IdentityDbContext<CoupleUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Gift> Gifts => Set<Gift>();
    public DbSet<Contribution> Contributions => Set<Contribution>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<WebhookInbox> WebhookInbox => Set<WebhookInbox>();
    public DbSet<StoredSession> Sessions => Set<StoredSession>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Gift>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(120); e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.ImageUrl).HasMaxLength(2048);
            e.ToTable(t => t.HasCheckConstraint("CK_Gift_Amount", "\"AmountCents\" BETWEEN 1 AND 100000000"));
        });
        builder.Entity<Contribution>(e =>
        {
            e.HasOne<Gift>().WithMany().HasForeignKey(x => x.GiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Status, x.CreatedAt, x.Id }); e.HasIndex(x => x.GiftId);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.GuestName).HasMaxLength(100); e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Message).HasMaxLength(500); e.Property(x => x.GiftTitleSnapshot).HasMaxLength(120);
            e.ToTable(t => { t.HasCheckConstraint("CK_Contribution_Status", "\"Status\" IN ('pending','approved','rejected','cancelled','refunded','charged_back')"); t.HasCheckConstraint("CK_Contribution_Currency", "\"Currency\" = 'BRL'"); });
        });
        builder.Entity<PaymentAttempt>(e =>
        {
            e.HasOne<Contribution>().WithMany().HasForeignKey(x => x.ContributionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ContributionId).IsUnique(); e.HasIndex(x => x.ProviderOrderId).IsUnique();
            e.HasIndex(x => x.ProviderIdempotencyKey).IsUnique(); e.HasIndex(x => x.NextSyncAt);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        builder.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(x => new { x.Operation, x.KeyHash });
            e.HasOne<Contribution>().WithMany().HasForeignKey(x => x.ContributionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<WebhookInbox>(e => { e.HasIndex(x => x.ProviderEventId).IsUnique(); e.HasIndex(x => new { x.ProcessedAt, x.ManualIntervention, x.NextAttemptAt }); });
        builder.Entity<StoredSession>().HasIndex(x => x.UserId);
    }
}
