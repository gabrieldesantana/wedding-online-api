using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wedding.Application;

namespace Wedding.Infrastructure;

public sealed class MercadoPagoClient(HttpClient http, IOptions<MercadoPagoOptions> settings, ILogger<MercadoPagoClient> logger) : IMercadoPagoClient
{
    public Task<ProviderOrder> CreateOrderAsync(string requestJson, Guid idempotencyKey, CancellationToken ct) => SendAsync(HttpMethod.Post, "v1/orders", requestJson, idempotencyKey, ct);
    public Task<ProviderOrder> GetOrderAsync(string id, CancellationToken ct) => SendAsync(HttpMethod.Get, "v1/orders/" + Uri.EscapeDataString(id), null, null, ct);
    private async Task<ProviderOrder> SendAsync(HttpMethod method, string path, string? json, Guid? key, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        ct = budget.Token;
        string? observedOrderId = null;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.AccessToken);
        if (key is not null) request.Headers.Add("X-Idempotency-Key", key.Value.ToString());
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            if (method == HttpMethod.Post && (settings.Value.Environment == "test" || settings.Value.SandboxSingleAttempt))
            {
                using var identityRequest = new HttpRequestMessage(HttpMethod.Get, "users/me");
                identityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.AccessToken);
                using var identityResponse = await http.SendAsync(identityRequest, ct);
                if (!identityResponse.IsSuccessStatusCode) throw new ApiException(502, "sandbox_account_unverified", "Conta de teste não verificada.");
                using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync(ct));
                if (identity.RootElement.GetProperty("id").ToString() != settings.Value.ReceiverId ||
                    !identity.RootElement.GetProperty("tags").EnumerateArray().Any(x => x.GetString() == "test_user"))
                    throw new ApiException(502, "sandbox_account_required", "Esta operação exige uma conta de teste verificada.");
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Provider request failed with HTTP {Status}", (int)response.StatusCode);
                OperationalMetrics.ProviderFailures.Add(1);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict or HttpStatusCode.Locked or >= HttpStatusCode.InternalServerError) throw ApiException.Unavailable();
                throw new ApiException(502, method == HttpMethod.Post && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity
                    ? "provider_creation_failed" : "provider_rejected", "O provedor não aceitou a operação.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            observedOrderId = root.TryGetProperty("id", out var orderIdentity) ? orderIdentity.ToString() : null;
            bool? accountLiveMode = null;
            if (!root.TryGetProperty("live_mode", out _))
            {
                // Orders responses may omit live_mode. Verify the authenticated receiver's test-user metadata instead.
                using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "users/me");
                accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.AccessToken);
                using var accountResponse = await http.SendAsync(accountRequest, ct);
                if (!accountResponse.IsSuccessStatusCode) throw new ApiException(502, "environment_unverified", "Não foi possível verificar o ambiente do provedor.");
                using var account = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync(ct));
                if (account.RootElement.GetProperty("id").ToString() != root.GetProperty("user_id").ToString()) throw new ApiException(502, "receiver_mismatch", "Resposta financeira incompatível.");
                accountLiveMode = !account.RootElement.GetProperty("tags").EnumerateArray().Any(x => x.GetString() == "test_user");
            }
            var order = Parse(root, accountLiveMode);
            // Sandbox legacy Payments projections may report live_mode=true and a merchant-order ID.
            // Do not consume those incompatible projections. Orders + verified test-account metadata
            // remain authoritative; unavailable sandbox fees/net amounts stay unknown.
            if (order.LiveMode && order.NetAmountCents is null && root.TryGetProperty("transactions", out var transactions) && transactions.TryGetProperty("payments", out var payments))
            {
                long net = 0, fees = 0; var known = false; var complete = true;
                foreach (var payment in payments.EnumerateArray())
                {
                    if (!payment.TryGetProperty("reference_id", out var reference) || !long.TryParse(reference.ToString(), out _)) { complete = false; continue; }
                    using var paymentRequest = new HttpRequestMessage(HttpMethod.Get, "v1/payments/" + Uri.EscapeDataString(reference.ToString()));
                    paymentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.AccessToken);
                    using var paymentResponse = await http.SendAsync(paymentRequest, ct);
                    if (!paymentResponse.IsSuccessStatusCode) { complete = false; continue; }
                    using var paymentDocument = JsonDocument.Parse(await paymentResponse.Content.ReadAsStringAsync(ct));
                    var financial = paymentDocument.RootElement;
                    if (financial.GetProperty("collector_id").ToString() != order.ReceiverId || financial.GetProperty("live_mode").GetBoolean() != order.LiveMode || financial.GetProperty("currency_id").GetString() != "BRL" ||
                        !financial.TryGetProperty("order", out var relation) || relation.GetProperty("id").ToString() != order.Id)
                        throw new ApiException(502, "financial_mismatch", "Resposta financeira incompatível.");
                    if (financial.TryGetProperty("transaction_details", out var details) && details.TryGetProperty("net_received_amount", out var received))
                    { net = checked(net + Rules.Cents(received.GetDecimal())); known = true; }
                    else complete = false;
                    if (financial.TryGetProperty("fee_details", out var feeDetails)) foreach (var fee in feeDetails.EnumerateArray()) fees = checked(fees + Rules.Cents(fee.GetProperty("amount").GetDecimal()));
                }
                if (known && complete) order = order with { NetAmountCents = net, FeeAmountCents = fees };
            }
            if (method == HttpMethod.Get && order.Id != Uri.UnescapeDataString(path["v1/orders/".Length..])) throw new ApiException(502, "resource_mismatch", "Resposta financeira incompatível.");
            return order;
        }
        catch (ApiException ex) when (observedOrderId is not null)
        {
            logger.LogError("Provider order {ProviderOrderId} requires verification: {Code}", observedOrderId, ex.Code);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            OperationalMetrics.ProviderFailures.Add(1);
            throw ApiException.Unavailable();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            logger.LogError("Provider order {ProviderOrderId} returned an unsupported response shape", observedOrderId);
            throw new ApiException(502, "invalid_provider_response", "Resposta inválida do provedor.");
        }
    }
    public static ProviderOrder Parse(JsonElement root, bool? verifiedAccountLiveMode = null)
    {
        static string Required(JsonElement item, string key) => item.GetProperty(key).ToString();
        static string? Optional(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
        static long Money(JsonElement item, string key) => Rules.Cents(decimal.Parse(Required(item, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
        static long? OptionalMoney(JsonElement item, string key) => Optional(item, key) is { } value ? Rules.Cents(decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)) : null;
        var url = Optional(root, "checkout_url");
        if (url is not null && !Rules.CheckoutUrl(url)) throw new ApiException(502, "invalid_checkout_url", "Resposta inválida do provedor.");
        // Do not infer environment from our own credentials: require authoritative resource metadata.
        var live = root.TryGetProperty("live_mode", out var liveMode) ? liveMode.GetBoolean() : verifiedAccountLiveMode ?? throw new ApiException(502, "environment_unverified", "Ambiente do provedor não verificado.");
        var updated = DateTimeOffset.Parse(Required(root, "last_updated_date"), CultureInfo.InvariantCulture).ToUniversalTime();
        DateTimeOffset? expires = Optional(root, "expiration_date") is { } expiration ? DateTimeOffset.Parse(expiration, CultureInfo.InvariantCulture).ToUniversalTime() : null;
        if (expires is null && Optional(root, "expiration_time") is { } duration && Optional(root, "created_date") is { } created)
            expires = DateTimeOffset.Parse(created, CultureInfo.InvariantCulture).ToUniversalTime() + System.Xml.XmlConvert.ToTimeSpan(duration);
        var refunded = OptionalMoney(root, "total_refunded_amount") ?? 0;
        var status = Required(root, "status");
        if (root.TryGetProperty("transactions", out var transactions))
        {
            if (transactions.TryGetProperty("refunds", out var refunds))
                refunded = Math.Max(refunded, refunds.EnumerateArray().Where(x => Optional(x, "status") == "processed").Sum(x => Money(x, "amount")));
            if (transactions.TryGetProperty("chargebacks", out var chargebacks) && chargebacks.EnumerateArray().Any(x => Optional(x, "status") is "in_process" or "settled")) status = "charged_back";
        }
        if (Rules.MapStatus(status, Optional(root, "status_detail"), refunded) == "approved" && Money(root, "total_paid_amount") < Money(root, "total_amount"))
            throw new ApiException(502, "uncaptured_payment", "Pagamento sem captura confirmada.");
        return new ProviderOrder(Required(root, "id"), Required(root, "external_reference"), Required(root, "user_id"), live,
            Required(root, "currency"), Money(root, "total_amount"), status, Optional(root, "status_detail"), updated, url,
            OptionalMoney(root, "net_received_amount"), OptionalMoney(root, "fee_amount"), refunded, expires, root.GetProperty("integration_data").GetProperty("application_id").ToString());
    }
}
