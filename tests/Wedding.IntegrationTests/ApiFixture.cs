using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Wedding.Application;
using Wedding.Infrastructure;
using Xunit;

namespace Wedding.IntegrationTests;

public sealed class ApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    public ApiFactory Factory { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("WEDDING_TEST_DATABASE");
        if (string.IsNullOrEmpty(connection))
        {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync(); connection = container.GetConnectionString();
        }
        // WEDDING_TEST_DATABASE must identify a disposable database. Tests apply migrations and create test data.
        await using (var db = new WeddingDbContext(new DbContextOptionsBuilder<WeddingDbContext>().UseNpgsql(connection).Options)) await db.Database.MigrateAsync();
        Factory = new ApiFactory(connection);
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CoupleUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roles.RoleExistsAsync("Couple")) Assert.True((await roles.CreateAsync(new IdentityRole<Guid>("Couple"))).Succeeded);
        foreach (var email in new[] { "bride@example.test", "groom@example.test" })
        {
            if (await users.FindByEmailAsync(email) is not null) continue;
            var user = new CoupleUser { Id = Guid.NewGuid(), Email = email, UserName = email, Name = "Test Couple", LockoutEnabled = true };
            Assert.True((await users.CreateAsync(user, "Test-Only-Password123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "Couple")).Succeeded);
        }
    }
    public async Task DisposeAsync() { if (Factory is not null) await Factory.DisposeAsync(); if (container is not null) await container.DisposeAsync(); }
    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
}
public sealed class ApiFactory(string connection) : WebApplicationFactory<Program>
{
    public string ConnectionString { get; } = connection;
    public int CreateLimit { get; init; } = 1000;
    public bool SingleAttemptSandbox { get; init; }
    public FakeProvider Provider { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var pair in new Dictionary<string, string>
        {
            ["ConnectionStrings:Default"] = ConnectionString, ["ConnectionStrings:Migrations"] = ConnectionString,
            ["Frontend:PublicOrigin"] = "https://site.example.test", ["Cors:AllowedOrigins:0"] = "https://site.example.test",
            ["MercadoPago:AccessToken"] = "test-only", ["MercadoPago:WebhookSecret"] = "fixture-only-secret",
            ["MercadoPago:ReceiverId"] = "receiver", ["MercadoPago:ApplicationId"] = "app", ["MercadoPago:Environment"] = "test",
            ["MercadoPago:NotificationUrl"] = "https://localhost/api/webhooks/mercadopago", ["MercadoPago:CheckoutEnabled"] = "true",
            ["MercadoPago:IdempotencyWindowMinutes"] = SingleAttemptSandbox ? "0" : "60", ["MercadoPago:SandboxSingleAttempt"] = SingleAttemptSandbox.ToString(), ["MercadoPago:ValidatedConfigJson"] = "{\"payment_method\":{\"not_allowed_types\":[\"ticket\"]}}",
            ["RateLimits:login"] = "1000", ["RateLimits:create"] = CreateLimit.ToString(), ["RateLimits:checkout"] = "1000", ["RateLimits:private"] = "1000"
        }) builder.UseSetting(pair.Key, pair.Value);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMercadoPagoClient>(); services.AddSingleton<IMercadoPagoClient>(Provider);
            var worker = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(InboxWorker));
            if (worker is not null) services.Remove(worker);
        });
    }
}
public sealed class FakeProvider : IMercadoPagoClient
{
    public ConcurrentDictionary<Guid, ProviderOrder> Orders { get; } = new();
    public ConcurrentDictionary<Guid, int> Calls { get; } = new();
    public ConcurrentDictionary<string, int> Reads { get; } = new();
    public bool TimeoutNext { get; set; }
    public Func<ProviderOrder, Task>? BeforeReturn { get; set; }
    public async Task<ProviderOrder> CreateOrderAsync(string requestJson, Guid key, CancellationToken ct)
    {
        Calls.AddOrUpdate(key, 1, (_, count) => count + 1);
        using var doc = JsonDocument.Parse(requestJson); var root = doc.RootElement;
        var order = Orders.GetOrAdd(key, _ => new ProviderOrder("ORD" + key.ToString("N"), root.GetProperty("external_reference").GetString()!, "receiver", false, "BRL",
            Rules.Cents(decimal.Parse(root.GetProperty("total_amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture)), "created", "created", DateTimeOffset.UtcNow,
            "https://www.mercadopago.com.br/checkout/test?order_id=" + key, null, null, 0, DateTimeOffset.UtcNow.AddDays(1), "app"));
        await Task.Delay(50, ct);
        if (BeforeReturn is { } callback) { BeforeReturn = null; await callback(order); }
        if (TimeoutNext) { TimeoutNext = false; throw ApiException.Unavailable(); }
        return order;
    }
    public async Task<ProviderOrder> GetOrderAsync(string id, CancellationToken ct)
    {
        Reads.AddOrUpdate(id, 1, (_, count) => count + 1); await Task.Delay(50, ct); return Orders.Values.Single(x => x.Id == id);
    }
}
[CollectionDefinition("API", DisableParallelization = true)] public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
