using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Wedding.Api;
using Wedding.Application;
using Wedding.Infrastructure;

var exportingOpenApi = args.FirstOrDefault() == "openapi";
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = exportingOpenApi ? "Development" : null });
if (builder.Environment.IsDevelopment() && !exportingOpenApi)
    builder.Configuration.AddJsonFile("appsettings.Development.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
if (exportingOpenApi)
{
    // Offline schema generation requires no credentials, database connection, or worker.
    builder.Environment.EnvironmentName = "Development";
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=schema;Username=unused;Password=unused",
        ["Frontend:PublicOrigin"] = "https://example.test", ["Cors:AllowedOrigins:0"] = "https://example.test",
        ["MercadoPago:AccessToken"] = "schema-only", ["MercadoPago:WebhookSecret"] = "schema-only",
        ["MercadoPago:ReceiverId"] = "schema-only", ["MercadoPago:ApplicationId"] = "schema-only",
        ["MercadoPago:NotificationUrl"] = "https://example.test/api/webhooks/mercadopago", ["MercadoPago:CheckoutEnabled"] = "false"
    });
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddRequestTimeouts(o => o.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
{
    Timeout = TimeSpan.FromSeconds(18), TimeoutStatusCode = 503,
    WriteTimeoutResponse = context => Problems.WriteAsync(context, 503, "Serviço temporariamente indisponível. Tente novamente.")
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024);
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port)) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var connection = builder.Configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");
var databaseConnection = DatabaseConnections.Normalize(connection, !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"));
builder.Services.AddDbContext<WeddingDbContext>(o => o.UseNpgsql(databaseConnection));
builder.Services.AddOptions<MercadoPagoOptions>().BindConfiguration("MercadoPago").ValidateDataAnnotations()
    .Validate(o => o.Environment is "test" or "production", "MercadoPago environment must be test or production.")
    .Validate(o => Rules.HttpsUrl(o.NotificationUrl), "Notification URL must use HTTPS.")
    .Validate(o => !o.SandboxSingleAttempt || ((builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")) && o.Environment == "test"), "Single-attempt sandbox mode is only available in Development/Testing with test credentials.")
    .Validate(o => !o.CheckoutEnabled || ((o.IdempotencyWindowMinutes > 0 || o.SandboxSingleAttempt) && ValidatedConfig.IsValid(o.ValidatedConfigJson)), "Checkout requires validated configuration and verified retention, or development-only single-attempt sandbox mode.")
    .ValidateOnStart();
builder.Services.AddOptions<FrontendOptions>().BindConfiguration("Frontend").ValidateDataAnnotations()
    .Validate(o => Uri.TryCreate(o.PublicOrigin, UriKind.Absolute, out var u) && string.IsNullOrEmpty(u.UserInfo) && u.AbsolutePath == "/" && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment) &&
        (u.Scheme == "https" || builder.Environment.IsDevelopment() && u.IsLoopback), "Frontend origin is invalid.").ValidateOnStart();
builder.Services.AddOptions<ProcessingOptions>().BindConfiguration("Processing")
    .Validate(o => o.PollSeconds is >= 1 and <= 60 && o.LeaseSeconds >= 30 && o.MaxAttempts is >= 1 and <= 10).ValidateOnStart();

builder.Services.AddIdentityCore<CoupleUser>(o =>
{
    o.Password.RequiredLength = 12;
    o.User.RequireUniqueEmail = true;
    o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<WeddingDbContext>().AddSignInManager().AddDefaultTokenProviders();
builder.Services.AddSingleton<DatabaseTicketStore>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme, o =>
{
    o.Cookie.Name = "Wedding.Session"; o.Cookie.HttpOnly = true; o.Cookie.Path = "/";
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.Cookie.SameSite = CookieSettings.ReadSameSite(builder.Configuration);
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = context => Problems.WriteAsync(context.HttpContext, 401, "Sessão necessária.");
    o.Events.OnRedirectToAccessDenied = context => Problems.WriteAsync(context.HttpContext, 403, "Acesso não permitido.");
});
builder.Services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme).Configure<DatabaseTicketStore>((o, store) => o.SessionStore = store);
builder.Services.AddAuthorization(o => o.AddPolicy("Couple", p => p.RequireAuthenticatedUser().RequireRole("Couple")));
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-Token"; o.Cookie.Name = "Wedding.Antiforgery"; o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.Cookie.SameSite = CookieSettings.ReadSameSite(builder.Configuration);
});
var protection = builder.Services.AddDataProtection().SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "WeddingGifts");
if (exportingOpenApi) protection.UseEphemeralDataProtectionProvider();
else protection.PersistKeysToDbContext<WeddingDbContext>();
if (builder.Configuration["DataProtection:CertificateBase64"] is { Length: > 0 } certificate)
{
    var cert = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(certificate), builder.Configuration["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
    if (!cert.HasPrivateKey) throw new InvalidOperationException("Data Protection certificate requires a private key.");
    protection.ProtectKeysWithCertificate(cert);
    var previous = builder.Configuration.GetSection("DataProtection:PreviousCertificates").GetChildren().Select(c =>
        X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(c["Base64"]!), c["Password"], X509KeyStorageFlags.EphemeralKeySet)).ToArray();
    protection.UnprotectKeysWithAnyCertificate([cert, .. previous]);
}
else if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing")) throw new InvalidOperationException("Data Protection certificate is required.");

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (origins.Length == 0 || origins.Any(o => !Rules.HttpsUrl(o) && !(builder.Environment.IsDevelopment() && Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback)))
    throw new InvalidOperationException("Explicit frontend origins are required.");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowCredentials().WithMethods("GET", "POST", "PUT", "OPTIONS")
    .WithHeaders("Content-Type", "X-CSRF-Token", "X-Contribution-Token", "Idempotency-Key")));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? []) o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? []) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
});
RateLimits.Configure(builder.Services, builder.Configuration);
builder.Services.AddScoped<TrackingTokens>(); builder.Services.AddScoped<ContributionService>();
builder.Services.AddScoped<CheckoutService>(); builder.Services.AddScoped<FinancialSync>();
builder.Services.AddScoped<WebhookService>(); builder.Services.AddScoped<DurableProcessor>();
builder.Services.AddHttpClient<IMercadoPagoClient, MercadoPagoClient>(o => { o.BaseAddress = new Uri("https://api.mercadopago.com/"); o.Timeout = TimeSpan.FromSeconds(10); })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
if (!exportingOpenApi) builder.Services.AddHostedService<InboxWorker>();
else
{
    var keyRingLoader = builder.Services.FirstOrDefault(d => d.ImplementationType?.Name == "DataProtectionHostedService");
    if (keyRingLoader is not null) builder.Services.Remove(keyRingLoader);
}
builder.Services.AddOpenApi(OpenApiContract.Configure);
var app = builder.Build();
if (args.Length > 0 && args[0] is "db" or "users" or "jobs")
{
    Environment.ExitCode = await AdministrativeCommands.RunAsync(app.Services, app.Configuration, args);
    return;
}
app.UseForwardedHeaders();
app.UseRequestTimeouts();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<RequestPolicyMiddleware>();
app.UseCors();
app.UseAuthentication(); app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<CsrfMiddleware>();
app.MapWeddingEndpoints();
if (app.Environment.IsDevelopment() || exportingOpenApi) app.MapOpenApi();
if (exportingOpenApi)
{
    await app.StartAsync();
    var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
    using var client = new HttpClient();
    var json = await client.GetStringAsync(addresses.Addresses.First() + "/openapi/v1.json");
    var output = args.Length == 2 ? args[1] : "openapi.json";
    await File.WriteAllTextAsync(output, json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    await app.StopAsync();
    return;
}
await app.RunAsync();
public partial class Program;
