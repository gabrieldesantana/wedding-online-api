using System.Net;

// Development helper: only the webhook path is reachable through the public tunnel.
// No configuration, credentials, cookies, catalog, authentication, or admin routes are exposed here.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5081");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024);
builder.Logging.ClearProviders();
using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
var app = builder.Build();
app.MapPost("/api/webhooks/mercadopago", async (HttpContext context) =>
{
    if (context.Request.Path.Value != "/api/webhooks/mercadopago") { context.Response.StatusCode = 404; return; }
    using var request = new HttpRequestMessage(HttpMethod.Post,
        "http://localhost:5080/api/webhooks/mercadopago" + context.Request.QueryString);
    request.Content = new StreamContent(context.Request.Body);
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    foreach (var header in new[] { "x-signature", "x-request-id" })
    {
        if (context.Request.Headers[header].Count == 1) request.Headers.TryAddWithoutValidation(header, context.Request.Headers[header].ToString());
    }
    try
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/problem+json";
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
    {
        if (!context.Response.HasStarted) { context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable; }
    }
});
await app.RunAsync();
