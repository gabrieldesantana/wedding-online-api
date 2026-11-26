using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Wedding.Api;
using Wedding.Application;
using Wedding.Infrastructure;
using Xunit;

namespace Wedding.UnitTests;

public sealed class RulesTests
{
    [Theory]
    [InlineData(null, "request", "order", "missing_signature")]
    [InlineData("ts=1,v1=bad", null, "order", "missing_request_id")]
    [InlineData("ts=1,v1=bad", "request", null, "missing_data_id_query")]
    [InlineData("ts=1,v1=bad", "request", "order", "malformed_signature")]
    public void SignatureDiagnosticsDoNotExposeIncomingValues(string? signature, string? request, string? id, string expected)
        => Assert.Equal(expected, WebhookSignature.FailureReason(signature, request, id, "fixture-only-secret"));

    [Fact] public void WrongHmacHasDistinctDiagnostic()
        => Assert.Equal("hmac_mismatch", WebhookSignature.FailureReason("ts=1,v1=" + new string('0', 64), "request", "order", "fixture-only-secret"));
    [Fact] public void NormalizedPayloadsHaveIdenticalFingerprints()
    {
        var id = Guid.NewGuid();
        Assert.Equal(Rules.Fingerprint(Rules.Normalize(new ContributionInput(id, " Maria ", "maria@example.com ", "  "))),
            Rules.Fingerprint(Rules.Normalize(new ContributionInput(id, "Maria", "maria@example.com", null))));
    }
    [Theory] [InlineData(0)] [InlineData(100000001)] public void RejectsGiftMoneyOutsideBounds(long amount) =>
        Assert.Equal(422, Assert.Throws<ApiException>(() => Rules.Normalize(new GiftInput("Gift", null, null, amount, true))).Status);
    [Theory] [InlineData("https://user:pass@example.com/a")] [InlineData("http://example.com/a")] [InlineData("/relative")]
    public void RejectsUnsafeImages(string image) => Assert.Throws<ApiException>(() => Rules.Normalize(new GiftInput("Gift", null, image, 100, true)));
    [Theory] [InlineData("https://mercadopago.com.br/checkout", true)] [InlineData("https://www.mercadopago.com.br/checkout", true)]
    [InlineData("https://mercadopago.com.br.evil.test/checkout", false)] [InlineData("https://user@mercadopago.com.br/checkout", false)] [InlineData("http://mercadopago.com.br/checkout", false)]
    public void CheckoutUrlUsesHostBoundary(string value, bool expected) => Assert.Equal(expected, Rules.CheckoutUrl(value));
    [Fact] public void MonetaryParsingRequiresExactCents()
    {
        Assert.Equal(15001, Rules.Cents(150.01m)); Assert.Throws<ApiException>(() => Rules.Cents(1.001m)); Assert.Throws<ApiException>(() => Rules.Cents(-1));
    }
    [Fact] public void TokensAreUniqueProtectedAndRecoverable()
    {
        var protection = new EphemeralDataProtectionProvider(); var tokens = new TrackingTokens(protection);
        var first = tokens.Create(); var second = tokens.Create();
        Assert.Equal(43, first.Token.Length); Assert.NotEqual(first.Token, second.Token); Assert.DoesNotContain(first.Token, first.Protected);
        Assert.True(tokens.Verify(first.Token, first.Hash)); Assert.False(tokens.Verify(second.Token, first.Hash));
        Assert.Equal(first.Token, new TrackingTokens(protection).Recover(first.Protected));
    }
    [Theory] [InlineData("processed", "accredited", 0, "approved")] [InlineData("processed", "accredited", 1, "refunded")]
    [InlineData("created", "created", 0, "pending")] [InlineData("charged_back", null, 0, "charged_back")]
    [InlineData("new_unknown_status", null, 0, null)] [InlineData("processed", "in_review", 0, null)]
    public void MapsOnlyKnownAuthoritativeStates(string status, string? detail, long refund, string? expected) => Assert.Equal(expected, Rules.MapStatus(status, detail, refund));
    [Fact] public void StrictDtoRejectsInjectedFinancialFields() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize<ContributionInput>("{\"giftId\":\"00000000-0000-0000-0000-000000000001\",\"guestName\":\"Maria\",\"email\":\"m@example.com\",\"status\":\"approved\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    [Fact] public void SignaturePreservesSignedIdAndChecksTampering()
    {
        const string secret = "fixture-only-secret", id = "ORD01JQ4S4KY8HWQ6NA5PXB65B3D3", request = "2066ca19-c6f1-498a-be75-1923005edd06", ts = "1742505638683";
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"id:{id};request-id:{request};ts:{ts};"))).ToLowerInvariant();
        var signature = $"ts={ts},v1={hash}";
        Assert.True(WebhookSignature.Verify(signature, request, id, secret));
        Assert.False(WebhookSignature.Verify(signature, request, id.ToLowerInvariant(), secret));
        Assert.False(WebhookSignature.Verify(signature, "changed", id, secret));
        Assert.False(WebhookSignature.Verify(signature, request, id, "wrong"));
    }
    [Theory] [InlineData("")] [InlineData("ts=bad,v1=bad")] [InlineData("ts=1,ts=2,v1=0000")]
    public void MalformedSignatureIsRejected(string signature) => Assert.False(WebhookSignature.Verify(signature, "req", "id", "secret"));
    [Fact] public void CheckoutConfigurationRequiresBoletoExclusionAndNoSubsidy()
    {
        Assert.True(ValidatedConfig.IsValid("{\"payment_method\":{\"not_allowed_types\":[\"ticket\"]}}"));
        Assert.True(ValidatedConfig.IsValid("{\"payment_method\":{\"not_allowed_types\":[\"ticket\"],\"installments_cost\":\"buyer\"}}"));
        Assert.False(ValidatedConfig.IsValid("{\"payment_method\":{\"not_allowed_types\":[\"ticket\"],\"installments_cost\":\"seller\"}}"));
        Assert.False(ValidatedConfig.IsValid("{}"));
    }
}
