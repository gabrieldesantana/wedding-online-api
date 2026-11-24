namespace Wedding.Domain;

public static class PaymentStates
{
    public static readonly string[] All = ["pending", "approved", "rejected", "cancelled", "refunded", "charged_back"];
}
public sealed class Gift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public long AmountCents { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Contribution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GiftId { get; set; }
    public string GiftTitleSnapshot { get; set; } = "";
    public long AmountCentsSnapshot { get; set; }
    public string Currency { get; set; } = "BRL";
    public string GuestName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Message { get; set; }
    public string Status { get; set; } = "pending";
    public long? NetAmountCents { get; set; }
    public long? GrossAmountCents { get; set; }
    public long? FeeAmountCents { get; set; }
    public long RefundedAmountCents { get; set; }
    public byte[] TrackingTokenHash { get; set; } = [];
    public string ProtectedTrackingToken { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastProviderSyncAt { get; set; }
    public long Version { get; set; }
}
public sealed class PaymentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContributionId { get; set; }
    public string Provider { get; set; } = "mercadopago";
    public Guid ProviderIdempotencyKey { get; set; } = Guid.NewGuid();
    public string? ProviderOrderId { get; set; }
    public string? CheckoutUrl { get; set; }
    public string CreationState { get; set; } = "creating";
    public string? ProviderStatus { get; set; }
    public DateTimeOffset? ProviderUpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? NextSyncAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseOwner { get; set; }
    public string ProtectedRequest { get; set; } = "";
    public DateTimeOffset? CheckoutExpiresAt { get; set; }
    public bool ManualIntervention { get; set; }
    public int SyncAttempts { get; set; }
    public long Version { get; set; }
}
public sealed class IdempotencyRecord
{
    public string Operation { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid ContributionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class WebhookInbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderEventId { get; set; } = "";
    public string Topic { get; set; } = "";
    public string ProviderResourceId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseOwner { get; set; }
    public string? LastErrorCode { get; set; }
    public bool ManualIntervention { get; set; }
}
