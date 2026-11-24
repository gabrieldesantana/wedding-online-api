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
    // Matches Mercado Pago's official .NET validator: data.id is preserved exactly, not lowercased.
    public static bool Verify(string? signature, string? requestId, string? dataId, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(dataId)) return false;
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in signature.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !parts.TryAdd(pair[0], pair[1])) return false;
        }
        if (!parts.TryGetValue("ts", out var timestamp) || !long.TryParse(timestamp, out _) || !parts.TryGetValue("v1", out var hash) || hash.Length != 64) return false;
        var manifest = $"id:{dataId.Trim()};request-id:{requestId.Trim()};ts:{timestamp};";
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        try { return CryptographicOperations.FixedTimeEquals(computed, Convert.FromHexString(hash)); }
        catch (FormatException) { return false; }
    }
}
public sealed class WebhookService(WeddingDbContext db, IOptions<MercadoPagoOptions> settings)
{
    public async Task ReceiveAsync(string body, string? signature, string? requestId, string? resourceId, CancellationToken ct)
    {
        if (!WebhookSignature.Verify(signature, requestId, resourceId, settings.Value.WebhookSecret))
            throw new ApiException(401, "invalid_signature", "Notificação não autenticada.");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != resourceId ||
            !root.TryGetProperty("application_id", out var app) || app.ToString() != settings.Value.ApplicationId ||
            !root.TryGetProperty("user_id", out var receiver) || receiver.ToString() != settings.Value.ReceiverId ||
            !root.TryGetProperty("live_mode", out var live) || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || live.GetBoolean() != (settings.Value.Environment == "production"))
            throw new ApiException(400, "invalid_webhook", "Notificação inválida.");
        var topic = type.GetString();
        if (string.IsNullOrEmpty(topic) || topic.Length > 80 || resourceId!.Length > 200) throw new ApiException(400, "invalid_webhook", "Notificação inválida.");
        var eventKey = root.TryGetProperty("id", out var eventId) && !string.IsNullOrWhiteSpace(eventId.ToString())
            ? "id:" + eventId.ToString() : "hash:" + Rules.Hash(signature + "|" + requestId + "|" + resourceId + "|" + body);
        var scopedKey = Rules.Hash(settings.Value.ApplicationId + ":" + settings.Value.Environment + ":" + topic + ":" + eventKey);
        db.WebhookInbox.Add(new WebhookInbox { ProviderEventId = scopedKey, Topic = topic, ProviderResourceId = resourceId });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { db.ChangeTracker.Clear(); }
    }
}
