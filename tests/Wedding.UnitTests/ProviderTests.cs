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
            Assert.Equal(key.ToString(), request.Headers.GetValues("X-Idempotency-Key").Single()); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("provider-private-error-with-guest-data") };
        });
        var client = new MercadoPagoClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com/") }, Options.Create(new MercadoPagoOptions { AccessToken = "test-only" }), NullLogger<MercadoPagoClient>.Instance);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.CreateOrderAsync("{}", key, CancellationToken.None));
        Assert.Equal("provider_creation_failed", error.Code); Assert.DoesNotContain("guest", error.Message);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
