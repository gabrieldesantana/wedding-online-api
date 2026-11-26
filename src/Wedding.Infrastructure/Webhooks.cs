using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wedding.Application;
using Wedding.Domain;

namespace Wedding.Infrastructure;

public static class WebhookSignature
{
    // The .NET SDK preserves case; some Mercado Pago notification flows use lowercase IDs.
    // Authenticate either canonical manifest, never an unsigned body or client payment status.
    public static bool Verify(string? signature, string? requestId, string? dataId, string secret)
        => FailureReason(signature, requestId, dataId, secret) is null;

    public static string? FailureReason(string? signature, string? requestId, string? dataId, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature)) return "missing_signature";
        if (string.IsNullOrWhiteSpace(requestId)) return "missing_request_id";
        if (string.IsNullOrWhiteSpace(dataId)) return "missing_data_id_query";
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in signature.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !parts.TryAdd(pair[0], pair[1])) return "malformed_signature";
        }
        if (!parts.TryGetValue("ts", out var timestamp) || !long.TryParse(timestamp, out _) || !parts.TryGetValue("v1", out var hash) || hash.Length != 64) return "malformed_signature";
        var manifest = $"id:{dataId.Trim()};request-id:{requestId.Trim()};ts:{timestamp};";
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        try
        {
            var received = Convert.FromHexString(hash);
            var exact = CryptographicOperations.FixedTimeEquals(computed, received);
            var canonicalManifest = $"id:{dataId.Trim().ToLowerInvariant()};request-id:{requestId.Trim()};ts:{timestamp};";
            var canonical = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonicalManifest));
            var lower = CryptographicOperations.FixedTimeEquals(canonical, received);
            return exact | lower ? null : "hmac_mismatch";
        }
        catch (FormatException) { return "malformed_signature"; }
    }
}
public sealed class WebhookService(WeddingDbContext db, IOptions<MercadoPagoOptions> settings, ILogger<WebhookService> logger)
{
    public async Task ReceiveAsync(string body, string? signature, string? requestId, string? resourceId, CancellationToken ct)
    {
        var signatureFailure = WebhookSignature.FailureReason(signature, requestId, resourceId, settings.Value.WebhookSecret);
        if (signatureFailure is not null)
        {
            logger.LogWarning("Webhook rejected: invalid_signature; reason {Reason}", signatureFailure);
            throw new ApiException(401, "invalid_signature", "Notificação não autenticada.");
        }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != resourceId)
        {
            throw Reject("invalid_shape_or_resource");
        }
        if (!root.TryGetProperty("application_id", out var app) || app.ToString() != settings.Value.ApplicationId) throw Reject("application_mismatch");
        if (!root.TryGetProperty("user_id", out var receiver) || receiver.ToString() != settings.Value.ReceiverId) throw Reject("receiver_mismatch");
        if (!root.TryGetProperty("live_mode", out var live) || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || live.GetBoolean() != (settings.Value.Environment == "production")) throw Reject("environment_mismatch");
        var topic = type.GetString();
        if (string.IsNullOrEmpty(topic) || topic.Length > 80 || resourceId!.Length > 200) throw new ApiException(400, "invalid_webhook", "Notificação inválida.");
        var eventKey = root.TryGetProperty("id", out var eventId) && !string.IsNullOrWhiteSpace(eventId.ToString())
            ? "id:" + eventId.ToString() : "hash:" + Rules.Hash(signature + "|" + requestId + "|" + resourceId + "|" + body);
        var scopedKey = Rules.Hash(settings.Value.ApplicationId + ":" + settings.Value.Environment + ":" + topic + ":" + eventKey);
        var item = new WebhookInbox { ProviderEventId = scopedKey, Topic = topic, ProviderResourceId = resourceId };
        db.WebhookInbox.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Webhook accepted into inbox {InboxId} for order {ProviderOrderId}", item.Id, resourceId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { db.ChangeTracker.Clear(); }
    }
    private ApiException Reject(string reason)
    {
        logger.LogWarning("Webhook rejected: {Reason}", reason);
        return new ApiException(400, "invalid_webhook", "Notificação inválida.");
    }
}
