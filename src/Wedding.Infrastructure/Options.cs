using System.ComponentModel.DataAnnotations;

namespace Wedding.Infrastructure;

public sealed class MercadoPagoOptions
{
    [Required] public string Environment { get; set; } = "test";
    [Required] public string AccessToken { get; set; } = "";
    [Required] public string WebhookSecret { get; set; } = "";
    [Required] public string ReceiverId { get; set; } = "";
    [Required] public string ApplicationId { get; set; } = "";
    [Required] public string NotificationUrl { get; set; } = "";
    // Must only be enabled after the account's Orders capabilities and payload have passed sandbox acceptance.
    public bool CheckoutEnabled { get; set; }
    // Development/test only: send once, never automatically repeat an uncertain creation without a verified retention window.
    public bool SandboxSingleAttempt { get; set; }
    public int IdempotencyWindowMinutes { get; set; }
    public string ValidatedConfigJson { get; set; } = "";
}
public sealed class FrontendOptions
{
    [Required] public string PublicOrigin { get; set; } = "";
}
public sealed class ProcessingOptions
{
    public int PollSeconds { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 10;
}
