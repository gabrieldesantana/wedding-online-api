using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wedding.Application;
using Wedding.Infrastructure;
using Xunit;

namespace Wedding.UnitTests;

public sealed class ProviderTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("id", false)]
    [InlineData("collector_id", false)]
    [InlineData("live_mode", false)]
    [InlineData("currency_id", false)]
    [InlineData("external_reference", false)]
    [InlineData("transaction_amount", false)]
    public async Task LiveFinancialDetailsUseExactLinkedPaymentRatherThanMerchantOrderId(string? tamperedField, bool nestedReference)
    {
        var node = JsonNode.Parse(Fixture("order-sandbox-created.json"))!;
        node["id"] = "ORD_LIVE_FIXTURE"; node["live_mode"] = true;
        node["status"] = "processed"; node["status_detail"] = "accredited"; node["total_paid_amount"] = "10.00";
        node["transactions"] = JsonNode.Parse(nestedReference
            ? "{\"payments\":[{\"reference\":{\"id\":\"123456\"},\"amount\":\"10.00\"}]}"
            : "{\"payments\":[{\"reference_id\":\"123456\",\"amount\":\"10.00\"}]}");
        var financial = JsonNode.Parse("""
            {"id":123456,"collector_id":"receiver","live_mode":true,"currency_id":"BRL",
             "external_reference":"00000000-0000-0000-0000-000000000001","transaction_amount":"10.00",
             "order":{"id":"987654","type":"mercadopago"},
             "transaction_details":{"net_received_amount":"9.50"},"fee_details":[{"amount":"0.50"}]}
            """)!;
        if (tamperedField == "live_mode") financial[tamperedField] = false;
        else if (tamperedField == "transaction_amount") financial[tamperedField] = "1.00";
        else if (tamperedField is not null) financial[tamperedField] = "wrong";
        var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(request.RequestUri!.AbsolutePath == "/v1/payments/123456" ? financial.ToJsonString() : node.ToJsonString()) });
        var client = new MercadoPagoClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com/") },
            Options.Create(new MercadoPagoOptions { Environment = "production" }), NullLogger<MercadoPagoClient>.Instance);
        if (tamperedField is not null)
            Assert.Equal("financial_mismatch", (await Assert.ThrowsAsync<ApiException>(() => client.GetOrderAsync("ORD_LIVE_FIXTURE", CancellationToken.None))).Code);
        else
        {
            var order = await client.GetOrderAsync("ORD_LIVE_FIXTURE", CancellationToken.None);
            Assert.Equal(950, order.NetAmountCents); Assert.Equal(50, order.FeeAmountCents);
            Assert.Equal("approved", Rules.MapStatus(order.Status, order.StatusDetail, order.RefundedAmountCents));
        }
    }
    [Theory]
    [InlineData("receiver", true, "production_account_required")]
    [InlineData("wrong-receiver", false, "receiver_mismatch")]
    public async Task ProductionRejectsIncompatibleCredentialsBeforePosting(string receiver, bool testAccount, string expected)
    {
        var posts = 0;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = receiver, tags = testAccount ? new[] { "test_user" } : new[] { "normal" } })) };
        });
        var client = new MercadoPagoClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com/") },
            Options.Create(new MercadoPagoOptions { Environment = "production", ReceiverId = "receiver" }), NullLogger<MercadoPagoClient>.Instance);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.CreateOrderAsync("{}", Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(expected, error.Code); Assert.Equal(0, posts);
    }
    [Fact] public async Task SandboxApprovalUsesOrdersWithoutLegacyFinancialProjection()
    {
        var node = JsonNode.Parse(Fixture("order-sandbox-created.json"))!;
        node["status"] = "processed"; node["status_detail"] = "accredited"; node["total_paid_amount"] = "10.00";
        node["transactions"] = JsonNode.Parse("{\"payments\":[{\"reference_id\":\"123456\"}]}");
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.DoesNotContain("/payments/", path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(path == "/users/me"
                ? "{\"id\":\"receiver\",\"tags\":[\"test_user\"]}" : node.ToJsonString()) };
        });
        var client = new MercadoPagoClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com/") },
            Options.Create(new MercadoPagoOptions { Environment = "test", ReceiverId = "receiver" }), NullLogger<MercadoPagoClient>.Instance);
        var order = await client.GetOrderAsync("ORDTST_SANITIZED_FIXTURE", CancellationToken.None);
        Assert.False(order.LiveMode); Assert.Equal("approved", Rules.MapStatus(order.Status, order.StatusDetail, order.RefundedAmountCents));
        Assert.Null(order.NetAmountCents); Assert.Null(order.FeeAmountCents);
    }
    [Fact] public async Task SingleAttemptSandboxRejectsARealAccountBeforeCreatingAnOrder()
    {
        var posts = 0;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"receiver\",\"tags\":[\"normal\"]}") };
        });
        var client = new MercadoPagoClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com/") },
            Options.Create(new MercadoPagoOptions { AccessToken = "test-only", ReceiverId = "receiver", SandboxSingleAttempt = true }), NullLogger<MercadoPagoClient>.Instance);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.CreateOrderAsync("{}", Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("sandbox_account_required", error.Code); Assert.Equal(0, posts);
    }
    private static string Fixture(string name = "order-created.json")
    {
        using var stream = typeof(ProviderTests).Assembly.GetManifestResourceStream($"Wedding.UnitTests.Fixtures.{name}")!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    [Fact] public void CapturedSandboxOrderMatchesAdapterContract()
    {
        using var document = JsonDocument.Parse(Fixture("order-sandbox-created.json"));
        var order = MercadoPagoClient.Parse(document.RootElement, false);
        Assert.Equal(1000, order.AmountCents);
        Assert.Equal("created", order.Status);
        Assert.Equal("app", order.ApplicationId);
        Assert.False(order.LiveMode);
    }
    [Fact] public void OfficialOrderShapeNeedsVerifiedEnvironment()
    {
        using var document = JsonDocument.Parse(Fixture());
        Assert.Equal("environment_unverified", Assert.Throws<ApiException>(() => MercadoPagoClient.Parse(document.RootElement)).Code);
        var order = MercadoPagoClient.Parse(document.RootElement, false);
        Assert.Equal(15000, order.AmountCents); Assert.Equal("app", order.ApplicationId); Assert.False(order.LiveMode);
        Assert.Equal(DateTimeOffset.Parse("2026-05-22T13:10:56.845Z"), order.ExpiresAt);
    }
    [Fact] public void RefundsComeFromProcessedTransactionsOnly()
    {
        var node = JsonNode.Parse(Fixture())!;
        node["transactions"] = JsonNode.Parse("{\"refunds\":[{\"amount\":\"1.00\",\"status\":\"processed\"},{\"amount\":\"2.00\",\"status\":\"processing\"}]}");
        using var doc = JsonDocument.Parse(node.ToJsonString()); var order = MercadoPagoClient.Parse(doc.RootElement, false);
        Assert.Equal(100, order.RefundedAmountCents); Assert.Equal("refunded", Rules.MapStatus(order.Status, order.StatusDetail, order.RefundedAmountCents));
    }
    [Fact] public void ProviderMoneyWithFractionsOfCentIsRejected()
    {
        var node = JsonNode.Parse(Fixture())!; node["total_amount"] = "150.001";
        using var doc = JsonDocument.Parse(node.ToJsonString()); Assert.Throws<ApiException>(() => MercadoPagoClient.Parse(doc.RootElement, false));
    }
    [Fact] public async Task ClientSendsPersistedIdempotencyKeyAndSanitizesFailures()
    {
        var key = Guid.NewGuid();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/users/me")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"receiver\",\"tags\":[\"normal\"]}") };
            Assert.Equal(key.ToString(), request.Headers.GetValues("X-Idempotency-Key").Single()); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("provider-private-error-with-guest-data") };
        });
        var client = new MercadoPagoClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com/") }, Options.Create(new MercadoPagoOptions { AccessToken = "test-only", Environment = "production", ReceiverId = "receiver" }), NullLogger<MercadoPagoClient>.Instance);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.CreateOrderAsync("{}", key, CancellationToken.None));
        Assert.Equal("provider_creation_failed", error.Code); Assert.DoesNotContain("guest", error.Message);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
