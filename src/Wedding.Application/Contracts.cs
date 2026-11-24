using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wedding.Domain;

namespace Wedding.Application;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GiftInput([property: JsonRequired] string Title, string? Description, string? ImageUrl, [property: JsonRequired] long AmountCents, [property: JsonRequired] bool Active);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContributionInput(Guid GiftId, string GuestName, string Email, string? Message);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginInput(string Email, string Password);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmptyInput;
public sealed record GiftDto(Guid Id, string Title, string? Description, string? ImageUrl, long AmountCents, bool Active)
{
    public static GiftDto From(Gift gift) => new(gift.Id, gift.Title, gift.Description, gift.ImageUrl, gift.AmountCents, gift.Active);
}
public sealed record ContributionDto(Guid Id, Guid GiftId, string GiftTitle, long AmountCents, long? NetAmountCents,
    string Currency, string GuestName, string Email, string? Message, string Status, DateTimeOffset CreatedAt)
{
    public static ContributionDto From(Contribution value) => new(value.Id, value.GiftId, value.GiftTitleSnapshot,
        value.AmountCentsSnapshot, value.NetAmountCents, value.Currency, value.GuestName, value.Email, value.Message, value.Status, value.CreatedAt);
}
public sealed record ContributionReceipt(Guid Id, string TrackingToken);
public sealed record CoupleUserDto(string Email, string Name);
public sealed record ContributionPage(IReadOnlyList<ContributionDto> Items, int Page, int PageSize, long Total);
public sealed record GiftSummary(long ApprovedCount, long GrossAmountCents, long? NetAmountCents);
public sealed record CheckoutResult(string CheckoutUrl);
public sealed record CsrfDto(string Token);

public sealed class ApiException(int status, string code, string detail, IDictionary<string, string[]>? errors = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IDictionary<string, string[]>? Errors { get; } = errors;
    public static ApiException Missing() => new(404, "not_found", "Recurso indisponível.");
    public static ApiException Conflict() => new(409, "conflict", "Operação incompatível com o estado atual.");
    public static ApiException Unavailable() => new(503, "temporarily_unavailable", "Serviço temporariamente indisponível. Tente novamente.");
}

public static class Rules
{
    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static ContributionInput Normalize(ContributionInput input)
    {
        var normalized = input with { GuestName = input.GuestName?.Trim() ?? "", Email = input.Email?.Trim() ?? "", Message = Optional(input.Message) };
        var errors = new Dictionary<string, string[]>();
        Length(errors, "guestName", normalized.GuestName, 2, 100);
        if (normalized.Email.Length > 254 || !MailAddress.TryCreate(normalized.Email, out var address) || address.Address != normalized.Email)
            errors["email"] = ["Informe um e-mail válido."];
        if (normalized.Message?.Length > 500) errors["message"] = ["Use até 500 caracteres."];
        if (normalized.GiftId == Guid.Empty) errors["giftId"] = ["Informe um presente válido."];
        Validate(errors);
        return normalized;
    }
    public static GiftInput Normalize(GiftInput input)
    {
        var normalized = input with { Title = input.Title?.Trim() ?? "", Description = Optional(input.Description), ImageUrl = Optional(input.ImageUrl) };
        var errors = new Dictionary<string, string[]>();
        Length(errors, "title", normalized.Title, 2, 120);
        if (normalized.Description?.Length > 500) errors["description"] = ["Use até 500 caracteres."];
        if (normalized.ImageUrl is { } image && (image.Length > 2048 || !HttpsUrl(image))) errors["imageUrl"] = ["Informe uma URL HTTPS válida."];
        if (normalized.AmountCents is < 1 or > 100000000) errors["amountCents"] = ["Informe um valor entre 1 e 100000000 centavos."];
        Validate(errors);
        return normalized;
    }
    private static void Length(Dictionary<string, string[]> errors, string key, string value, int min, int max)
    {
        if (value.Length < min || value.Length > max) errors[key] = [$"Use entre {min} e {max} caracteres."];
    }
    private static void Validate(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0) throw new ApiException(422, "validation", "Confira os campos informados.", errors);
    }
    public static bool HttpsUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && string.IsNullOrEmpty(url.UserInfo);
    public static bool CheckoutUrl(string value) => HttpsUrl(value) && (new Uri(value).Host == "mercadopago.com.br" || new Uri(value).Host.EndsWith(".mercadopago.com.br", StringComparison.OrdinalIgnoreCase));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Fingerprint<T>(T value) => Hash(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    public static Guid IdempotencyKey(string? value) => Guid.TryParseExact(value, "D", out var id) ? id : throw new ApiException(400, "invalid_key", "Informe Idempotency-Key como UUID.");
    public static long Cents(decimal amount)
    {
        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents) || cents < 0 || cents > long.MaxValue) throw new ApiException(502, "invalid_money", "Resposta financeira inválida.");
        return (long)cents;
    }
    public static string? MapStatus(string status, string? detail, long refunded) => refunded > 0 ? "refunded" : status switch
    {
        "created" or "processing" or "action_required" => "pending",
        "processed" when detail is "accredited" or "partially_refunded" => detail == "partially_refunded" ? "refunded" : "approved",
        "approved" or "captured" => "approved",
        "failed" or "rejected" => "rejected",
        "cancelled" or "canceled" or "expired" => "cancelled",
        "refunded" => "refunded",
        "charged_back" or "charged_back_in_process" => "charged_back",
        _ => null
    };
}

public sealed record ProviderOrder(string Id, string Reference, string ReceiverId, bool LiveMode, string Currency,
    long AmountCents, string Status, string? StatusDetail, DateTimeOffset UpdatedAt, string? CheckoutUrl,
    long? NetAmountCents, long? FeeAmountCents, long RefundedAmountCents, DateTimeOffset? ExpiresAt, string ApplicationId = "");
public interface IMercadoPagoClient
{
    Task<ProviderOrder> CreateOrderAsync(string requestJson, Guid idempotencyKey, CancellationToken cancellationToken);
    Task<ProviderOrder> GetOrderAsync(string id, CancellationToken cancellationToken);
}
