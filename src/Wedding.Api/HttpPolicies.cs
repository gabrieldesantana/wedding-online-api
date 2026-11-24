using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wedding.Application;

namespace Wedding.Api;

public static class CookieSettings
{
    public static SameSiteMode ReadSameSite(IConfiguration config) => config["Authentication:CookieSameSite"] switch
    {
        null or "Lax" => SameSiteMode.Lax, "None" => SameSiteMode.None,
        _ => throw new InvalidOperationException("CookieSameSite must be Lax or None.")
    };
}
public static class Problems
{
    public static Task WriteAsync(HttpContext context, int status, string detail, IDictionary<string, string[]>? errors = null)
    {
        context.Response.StatusCode = status;
        if (status is 429 or 503) context.Response.Headers.RetryAfter = "5";
        var problem = new ProblemDetails { Type = "about:blank", Status = status, Title = status switch
        { 400 or 422 => "Dados inválidos", 401 => "Não autenticado", 403 => "Acesso negado", 404 => "Recurso indisponível", 409 => "Conflito", 429 => "Muitas solicitações", 502 or 503 => "Serviço indisponível", _ => "Erro interno" }, Detail = detail };
        if (errors is not null) problem.Extensions["errors"] = errors;
        return context.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null, contentType: "application/problem+json");
    }
}
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400 && !context.Response.HasStarted && context.Response.ContentType is null)
                await Problems.WriteAsync(context, context.Response.StatusCode, "Não foi possível concluir a solicitação.");
        }
        catch (ApiException ex) { await Problems.WriteAsync(context, ex.Status, ex.Message, ex.Errors); }
        catch (Exception ex) when (ex is BadHttpRequestException or JsonException or FormatException)
        { await Problems.WriteAsync(context, 400, "Solicitação malformada."); }
        catch (Exception ex) when (ex is NpgsqlException or DbUpdateException or TimeoutException || ex.InnerException is NpgsqlException)
        { logger.LogError("Database operation unavailable for request {RequestId}", context.TraceIdentifier); await Problems.WriteAsync(context, 503, "Serviço temporariamente indisponível."); }
        catch (OperationCanceledException) when (context.Features.Get<Microsoft.AspNetCore.Http.Timeouts.IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested == true) { throw; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested &&
            context.Features.Get<Microsoft.AspNetCore.Http.Timeouts.IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested != true) { }
        catch { logger.LogError("Unhandled failure for request {RequestId}", context.TraceIdentifier); await Problems.WriteAsync(context, 500, "Não foi possível concluir a solicitação."); }
    }
}
public sealed class RequestPolicyMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Path.StartsWithSegments("/api/auth") || context.Request.Path.StartsWithSegments("/api/contributions") || context.Request.Path.StartsWithSegments("/api/admin")) context.Response.Headers.CacheControl = "no-store";
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing") && !context.Request.IsHttps && context.Request.Path.StartsWithSegments("/api"))
            throw new ApiException(400, "https_required", "Use HTTPS.");
        var limit = context.Request.Path == "/api/webhooks/mercadopago" ? 256 * 1024 : 16 * 1024;
        if (context.Request.ContentLength > limit) throw new ApiException(400, "body_too_large", "Corpo da solicitação excede o limite.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit;
        await next(context);
    }
}
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/webhooks/mercadopago" && context.Request.Method is "POST" or "PUT")
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { throw new ApiException(403, "csrf_invalid", "Sessão segura inválida. Tente novamente."); }
        }
        await next(context);
    }
}
public static class RateLimits
{
    public static void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddRateLimiter(o =>
        {
            o.OnRejected = async (context, _) => await Problems.WriteAsync(context.HttpContext, 429, "Tente novamente em instantes.");
            Add("login", 5, false); Add("create", 10, false); Add("checkout", 10, true); Add("private", 30, true);
            void Add(string policy, int initial, bool contribution)
            {
                var limit = config.GetValue<int?>("RateLimits:" + policy) ?? initial;
                if (limit <= 0) throw new InvalidOperationException("Rate limits must be positive.");
                o.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
                    (context.Connection.RemoteIpAddress?.ToString() ?? "unknown") + (contribution ? ":" + context.Request.RouteValues["id"] : ""),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            }
        });
    }
}
public static class ValidatedConfig
{
    public static bool IsValid(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var method = doc.RootElement.GetProperty("payment_method");
            // Sandbox-approved configuration must explicitly disable boleto and avoid seller-funded interest-free installments.
            var exclusions = method.GetProperty("not_allowed_types").EnumerateArray().Select(x => x.GetString()).ToArray();
            return exclusions.Contains("ticket") && !exclusions.Contains("credit_card") && !exclusions.Contains("bank_transfer") &&
                (!method.TryGetProperty("not_allowed_ids", out var ids) || !ids.EnumerateArray().Any(x => x.GetString() == "pix")) &&
                (!method.TryGetProperty("installments", out var installments) || !installments.TryGetProperty("interest_free", out _)) &&
                (!method.TryGetProperty("installments_cost", out var cost) || cost.GetString() == "buyer");
        }
        catch { return false; }
    }
}
