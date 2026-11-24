using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wedding.Application;
using Wedding.Domain;
using Wedding.Infrastructure;
using Xunit;

namespace Wedding.IntegrationTests;

[Collection("API")]
public sealed class ContractTests(ApiFixture fixture)
{
    [Fact] public async Task SingleAttemptSandboxWorksWithoutRetentionAndNeverRetriesUncertainty()
    {
        await using var factory = new ApiFactory(fixture.Factory.ConnectionString) { SingleAttemptSandbox = true };
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var csrf = await Csrf(client); var gift = await GiftAsync();
        var created = await Post(client, "/api/contributions", new ContributionInput(gift.Id, "Sandbox Guest", "sandbox@example.test", null), csrf, Guid.NewGuid().ToString());
        var receipt = (await created.Content.ReadFromJsonAsync<ContributionReceipt>())!;
        factory.Provider.TimeoutNext = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ContributionId == receipt.Id);
        Assert.True(attempt.ManualIntervention); Assert.Equal("uncertain", attempt.CreationState);
        await db.PaymentAttempts.Where(x => x.Id == attempt.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.False(await scope.ServiceProvider.GetRequiredService<DurableProcessor>().ProcessAttemptAsync(receipt.Id, null, null, CancellationToken.None));
        Assert.Equal(1, factory.Provider.Calls[attempt.ProviderIdempotencyKey]);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken)).StatusCode);
    }
    [Fact] public async Task RateLimitAndBodyLimitReturnProblemDetails()
    {
        await using var limited = new ApiFactory(fixture.Factory.ConnectionString) { CreateLimit = 1 };
        using var client = limited.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var csrf = await Csrf(client); var gift = await GiftAsync(); var input = new ContributionInput(gift.Id, "Maria Silva", "maria@example.test", null);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, "/api/contributions", input, csrf, Guid.NewGuid().ToString())).StatusCode);
        var rejected = await Post(client, "/api/contributions", input, csrf, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode); Assert.NotNull(rejected.Headers.RetryAfter); Assert.True(rejected.Headers.CacheControl!.NoStore);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType!.MediaType);
        var oversized = await client.PostAsync("/api/contributions", new StringContent(new string('x', 17 * 1024), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }
    [Fact] public async Task IdempotencyWindowExpiryNeverCreatesAnotherOrder()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            fixture.Factory.Provider.TimeoutNext = true;
            await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken);
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ContributionId == receipt.Id);
            await db.PaymentAttempts.Where(x => x.Id == attempt.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddHours(-2)).SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            await scope.ServiceProvider.GetRequiredService<DurableProcessor>().ProcessAttemptAsync(receipt.Id, null, null, CancellationToken.None);
            Assert.Equal(1, fixture.Factory.Provider.Calls[attempt.ProviderIdempotencyKey]);
            Assert.True((await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id)).ManualIntervention);
        }
    }
    [Fact] public async Task ExhaustedInboxIsRetainedForManualIntervention()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        var item = new WebhookInbox { ProviderEventId = Guid.NewGuid().ToString(), ProviderResourceId = "missing-order", Topic = "order", Attempts = 9 };
        db.WebhookInbox.Add(item); await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<DurableProcessor>().ProcessInboxAsync(CancellationToken.None);
        var failed = await db.WebhookInbox.AsNoTracking().SingleAsync(x => x.Id == item.Id);
        Assert.True(failed.ManualIntervention); Assert.Equal(10, failed.Attempts); Assert.Null(failed.ProcessedAt); Assert.NotNull(failed.LastErrorCode);
    }
    [Fact] public async Task InvalidInputsPersistNothingAndErrorsHaveNoHtml()
    {
        using var client = fixture.Client(); var csrf = await Csrf(client); var gift = await GiftAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>(); var before = await db.Contributions.CountAsync();
        var invalid = await Post(client, "/api/contributions", new ContributionInput(gift.Id, "X", "invalid", null), csrf, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode); Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        var tampered = await Post(client, "/api/contributions", new { giftId = gift.Id, guestName = "Maria", email = "maria@example.test", amountCents = 1, status = "approved" }, csrf, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode); Assert.Equal(before, await db.Contributions.CountAsync());
    }
    [Fact] public async Task AdminHistoryAndSummaryUseCurrentFinancialState()
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/api/auth/login", new { email = "bride@example.test", password = "Test-Only-Password123!" }, await Csrf(client))).StatusCode);
        var invalid = await client.GetAsync("/api/admin/contributions?page=0"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/contributions?status=unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/contributions?pageSize=101")).StatusCode);
        var (guest, _, receipt) = await ContributionAsync(); guest.Dispose();
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        await db.Contributions.Where(x => x.Id == receipt.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "approved"));
        var approved = db.Contributions.Where(x => x.Status == "approved");
        var summary = await client.GetFromJsonAsync<GiftSummary>("/api/admin/summary");
        Assert.Equal(await approved.LongCountAsync(), summary!.ApprovedCount); Assert.Null(summary.NetAmountCents);
        var page = await client.GetFromJsonAsync<ContributionPage>("/api/admin/contributions?page=999999&pageSize=10&status=approved");
        Assert.Empty(page!.Items); Assert.Equal(summary.ApprovedCount, page.Total);
        await db.Contributions.Where(x => x.Id == receipt.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "refunded"));
        Assert.Equal(summary.ApprovedCount - 1, (await client.GetFromJsonAsync<GiftSummary>("/api/admin/summary"))!.ApprovedCount);
    }
    [Fact] public async Task PersistedKeysRecoverTokensAndSessionsAcrossHosts()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            var login = await Post(client, "/api/auth/login", new { email = "bride@example.test", password = "Test-Only-Password123!" }, csrf);
            var sessionCookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Wedding.Session=", StringComparison.Ordinal)).Split(';')[0];
            await using var second = new ApiFactory(fixture.Factory.ConnectionString);
            using var restarted = second.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"); me.Headers.Add("Cookie", sessionCookie);
            Assert.Equal(HttpStatusCode.OK, (await restarted.SendAsync(me)).StatusCode);
            await using var scope = second.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
            var saved = await db.Contributions.AsNoTracking().SingleAsync(x => x.Id == receipt.Id);
            Assert.Equal(receipt.TrackingToken, scope.ServiceProvider.GetRequiredService<TrackingTokens>().Recover(saved.ProtectedTrackingToken));
        }
    }
    [Fact] public async Task ResetPasswordCommandRevokesAllUserSessions()
    {
        using var first = fixture.Client(); using var second = fixture.Client();
        foreach (var client in new[] { first, second }) Assert.Equal(HttpStatusCode.OK, (await Post(client, "/api/auth/login", new { email = "groom@example.test", password = "Test-Only-Password123!" }, await Csrf(client))).StatusCode);
        Environment.SetEnvironmentVariable("WEDDING_TEMP_PASSWORD", "Test-Only-Password123!");
        var code = await Wedding.Api.AdministrativeCommands.RunAsync(fixture.Factory.Services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), ["users", "reset-password", "groom@example.test"]);
        Assert.Equal(0, code);
        foreach (var client in new[] { first, second }) Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
    [Fact] public async Task ApprovalBeforeCreateResponseDoesNotGetOverwritten()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            fixture.Factory.Provider.BeforeReturn = async order =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FinancialSync>().ApplyAsync(order with { Status = "processed", StatusDetail = "accredited", UpdatedAt = order.UpdatedAt.AddSeconds(1) }, CancellationToken.None);
            };
            Assert.Equal(HttpStatusCode.OK, (await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken)).StatusCode);
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
            Assert.Equal("approved", (await db.Contributions.AsNoTracking().SingleAsync(x => x.Id == receipt.Id)).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken)).StatusCode);
        }
    }
    [Fact] public async Task OverlappingProcessorsClaimInboxOnlyOnceAndExpiredLeasesRecover()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken);
            await using var first = fixture.Factory.Services.CreateAsyncScope(); await using var second = fixture.Factory.Services.CreateAsyncScope();
            var db = first.ServiceProvider.GetRequiredService<WeddingDbContext>(); var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ContributionId == receipt.Id);
            var item = new WebhookInbox { ProviderEventId = Guid.NewGuid().ToString(), ProviderResourceId = attempt.ProviderOrderId!, Topic = "order", LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1), LeaseOwner = Guid.NewGuid() };
            db.WebhookInbox.Add(item); await db.SaveChangesAsync();
            await Task.WhenAll(first.ServiceProvider.GetRequiredService<DurableProcessor>().ProcessInboxAsync(CancellationToken.None), second.ServiceProvider.GetRequiredService<DurableProcessor>().ProcessInboxAsync(CancellationToken.None));
            Assert.Equal(1, fixture.Factory.Provider.Reads[attempt.ProviderOrderId!]);
            var processed = await db.WebhookInbox.AsNoTracking().SingleAsync(x => x.Id == item.Id); Assert.NotNull(processed.ProcessedAt); Assert.Null(processed.LeaseOwner);
        }
    }
    private static async Task<string> Csrf(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString()!;
    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, object body, string csrf, string? key = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-Token", csrf);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (token is not null) request.Headers.Add("X-Contribution-Token", token);
        return await client.SendAsync(request);
    }
    private async Task<Gift> GiftAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        var gift = new Gift { Title = "A dinner together", AmountCents = 15000, Active = true }; db.Gifts.Add(gift); await db.SaveChangesAsync(); return gift;
    }
    private async Task<(HttpClient Client, string Csrf, ContributionReceipt Receipt)> ContributionAsync()
    {
        var client = fixture.Client(); var csrf = await Csrf(client); var gift = await GiftAsync();
        var response = await Post(client, "/api/contributions", new ContributionInput(gift.Id, "Maria Silva", "maria@example.test", null), csrf, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, csrf, (await response.Content.ReadFromJsonAsync<ContributionReceipt>())!);
    }
    [Fact] public async Task HealthAndCatalogHaveNoPersonalData()
    {
        using var client = fixture.Client(); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var gift = await GiftAsync(); var json = await client.GetStringAsync("/api/gifts");
        Assert.Contains(gift.Id.ToString(), json); Assert.DoesNotContain("email", json); Assert.DoesNotContain("trackingToken", json); Assert.Contains("\"description\":null", json);
    }
    [Fact] public async Task AuthAndCsrfUseProblemJsonAndNoStore()
    {
        using var client = fixture.Client(); var unauthorized = await client.GetAsync("/api/admin/gifts");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode); Assert.Equal("application/problem+json", unauthorized.Content.Headers.ContentType!.MediaType);
        Assert.True(unauthorized.Headers.CacheControl!.NoStore);
        var missing = await client.PostAsJsonAsync("/api/auth/login", new { email = "bride@example.test", password = "Test-Only-Password123!" });
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        foreach (var email in new[] { "bride@example.test", "groom@example.test" })
        {
            var login = await Post(client, "/api/auth/login", new { email, password = "Test-Only-Password123!" }, await Csrf(client));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Contains(login.Headers.GetValues("Set-Cookie"), x => x.Contains("httponly", StringComparison.OrdinalIgnoreCase) && x.Contains("secure", StringComparison.OrdinalIgnoreCase) && !x.Contains("domain=", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "/api/auth/logout", new { }, await Csrf(client))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }
    [Fact] public async Task PreflightDoesNotRequireSessionOrCsrf()
    {
        using var client = fixture.Client(); using var request = new HttpRequestMessage(HttpMethod.Options, "/api/contributions");
        request.Headers.Add("Origin", "https://site.example.test"); request.Headers.Add("Access-Control-Request-Method", "POST"); request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token,idempotency-key");
        var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }
    [Fact] public async Task ConcurrentCreateHasSameReceiptAndReplaySurvivesDeactivation()
    {
        using var client = fixture.Client(); var csrf = await Csrf(client); var gift = await GiftAsync(); var key = Guid.NewGuid().ToString();
        var input = new ContributionInput(gift.Id, "Maria Silva", "maria@example.test", " ");
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(client, "/api/contributions", input, csrf, key)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        var receipts = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<ContributionReceipt>()));
        Assert.Single(receipts.Distinct());
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        await db.Gifts.Where(x => x.Id == gift.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false).SetProperty(x => x.Title, "Changed"));
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/api/contributions", input, csrf, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, "/api/contributions", input with { GuestName = "Different Guest" }, csrf, key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, "/api/contributions", input, csrf, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(gift.Title, (await db.Contributions.SingleAsync(x => x.Id == receipts[0]!.Id)).GiftTitleSnapshot);
    }
    [Fact] public async Task TokensCannotAccessOtherContributionAndAreNeverReturnedByRead()
    {
        var (client, _, receipt) = await ContributionAsync(); using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/contributions/" + receipt.Id); request.Headers.Add("X-Contribution-Token", receipt.TrackingToken);
            var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(receipt.TrackingToken, json); Assert.Contains("\"netAmountCents\":null", json);
            var wrong = await client.GetAsync("/api/contributions/" + receipt.Id); var missing = await client.GetAsync("/api/contributions/" + Guid.NewGuid());
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode); Assert.Equal(await wrong.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
        }
    }
    [Fact] public async Task ConcurrentCheckoutCreatesOneProviderOrder()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken)));
            Assert.Contains(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.All(responses, x => Assert.Contains(x.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable }));
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
            var attempt = await db.PaymentAttempts.SingleAsync(x => x.ContributionId == receipt.Id);
            Assert.Equal(1, fixture.Factory.Provider.Calls[attempt.ProviderIdempotencyKey]);
        }
    }
    [Fact] public async Task TimeoutRecoveryReusesProviderKey()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            fixture.Factory.Provider.TimeoutNext = true;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken)).StatusCode);
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ContributionId == receipt.Id); Assert.Equal("uncertain", attempt.CreationState);
            await db.PaymentAttempts.Where(x => x.Id == attempt.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            await scope.ServiceProvider.GetRequiredService<DurableProcessor>().ProcessAttemptAsync(receipt.Id, null, null, CancellationToken.None);
            Assert.Equal(2, fixture.Factory.Provider.Calls[attempt.ProviderIdempotencyKey]);
            Assert.Equal("ready", (await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt.Id)).CreationState);
        }
    }
    [Fact] public async Task FinancialMismatchAndStaleReadNeverOverwriteApproval()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken)).StatusCode);
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>(); var sync = scope.ServiceProvider.GetRequiredService<FinancialSync>();
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ContributionId == receipt.Id); var order = fixture.Factory.Provider.Orders[attempt.ProviderIdempotencyKey];
            foreach (var invalid in new[] { order with { AmountCents = 1 }, order with { ReceiverId = "wrong" }, order with { LiveMode = true }, order with { ApplicationId = "wrong" } })
                await Assert.ThrowsAsync<ApiException>(() => sync.ApplyAsync(invalid, CancellationToken.None));
            var approved = order with { Status = "processed", StatusDetail = "accredited", NetAmountCents = 14500, UpdatedAt = order.UpdatedAt.AddMinutes(1) };
            await sync.ApplyAsync(approved, CancellationToken.None); await sync.ApplyAsync(order, CancellationToken.None);
            Assert.Equal("approved", (await db.Contributions.AsNoTracking().SingleAsync(x => x.Id == receipt.Id)).Status);
            await sync.ApplyAsync(approved with { RefundedAmountCents = 1, UpdatedAt = approved.UpdatedAt.AddMinutes(1) }, CancellationToken.None);
            Assert.Equal("refunded", (await db.Contributions.AsNoTracking().SingleAsync(x => x.Id == receipt.Id)).Status);
        }
    }
    [Fact] public async Task WebhookDeduplicatesAndQueriesAuthoritativeOrder()
    {
        var (client, csrf, receipt) = await ContributionAsync(); using (client)
        {
            await Post(client, $"/api/contributions/{receipt.Id}/checkout", new { }, csrf, Guid.NewGuid().ToString(), receipt.TrackingToken);
            await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ContributionId == receipt.Id);
            var order = fixture.Factory.Provider.Orders[attempt.ProviderIdempotencyKey];
            var eventId = Guid.NewGuid().ToString(); var requestId = Guid.NewGuid().ToString(); const string ts = "1742505638683";
            var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("fixture-only-secret"), Encoding.UTF8.GetBytes($"id:{order.Id};request-id:{requestId};ts:{ts};")));
            async Task<HttpResponseMessage> Send(string signature)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/mercadopago?data.id=" + order.Id)
                { Content = JsonContent.Create(new { id = eventId, type = "order", application_id = "app", user_id = "receiver", live_mode = false, status = "approved", data = new { id = order.Id } }) };
                request.Headers.Add("x-signature", signature); request.Headers.Add("x-request-id", requestId); return await client.SendAsync(request);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send("invalid")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send($"ts={ts},v1={hash}")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await Send($"ts={ts},v1={hash}")).StatusCode);
            await scope.ServiceProvider.GetRequiredService<DurableProcessor>().ProcessInboxAsync(CancellationToken.None);
            Assert.Equal("pending", (await db.Contributions.AsNoTracking().SingleAsync(x => x.Id == receipt.Id)).Status);
            Assert.Single(await db.WebhookInbox.Where(x => x.ProviderResourceId == order.Id).ToListAsync());
        }
    }
}
