using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wedding.Application;
using Wedding.Domain;
using Wedding.Infrastructure;

namespace Wedding.Api;

public static class Endpoints
{
    public static void MapWeddingEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", async (WeddingDbContext db, CancellationToken ct) =>
        {
            try { return await db.Database.CanConnectAsync(ct) && !((await db.Database.GetPendingMigrationsAsync(ct)).Any()) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503); }
            catch { return Results.StatusCode(503); }
        });
        var auth = app.MapGroup("/api/auth").WithTags("Session");
        auth.MapGet("/csrf", (HttpContext http, IAntiforgery csrf) => Results.Ok(new CsrfDto(csrf.GetAndStoreTokens(http).RequestToken!))).Produces<CsrfDto>();
        auth.MapPost("/login", async (LoginInput input, SignInManager<CoupleUser> signIn, UserManager<CoupleUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrEmpty(input.Password) || input.Email.Length > 254 || input.Password.Length > 1024)
                throw new ApiException(401, "invalid_credentials", "E-mail ou senha inválidos.");
            var user = await users.FindByEmailAsync(input.Email.Trim());
            if (user is null)
            {
                // Use the configured hasher even for nonexistent users to reduce account enumeration timing differences.
                var dummy = new CoupleUser();
                var hasher = users.PasswordHasher;
                hasher.VerifyHashedPassword(dummy, DummyPassword.Hash, input.Password);
                throw new ApiException(401, "invalid_credentials", "E-mail ou senha inválidos.");
            }
            var result = await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true);
            if (!result.Succeeded || !await users.IsInRoleAsync(user, "Couple")) throw new ApiException(401, "invalid_credentials", "E-mail ou senha inválidos.");
            await signIn.SignInAsync(user, new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
            return Results.Ok(new CoupleUserDto(user.Email!, user.Name));
        }).RequireRateLimiting("login").Produces<CoupleUserDto>();
        auth.MapGet("/me", async (ClaimsPrincipal principal, UserManager<CoupleUser> users) =>
        {
            var user = await users.GetUserAsync(principal) ?? throw new ApiException(401, "session_required", "Sessão necessária.");
            return Results.Ok(new CoupleUserDto(user.Email!, user.Name));
        }).RequireAuthorization("Couple").Produces<CoupleUserDto>();
        auth.MapPost("/logout", async (EmptyInput input, SignInManager<CoupleUser> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); }).RequireAuthorization("Couple").Produces(204);

        app.MapGet("/api/gifts", async (WeddingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Gifts.AsNoTracking().Where(x => x.Active).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct)).Select(GiftDto.From).ToArray())).WithTags("Gifts").Produces<GiftDto[]>();
        app.MapPost("/api/contributions", async (ContributionInput input, HttpContext http, ContributionService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(input, Header(http, "Idempotency-Key"), ct);
            return result.Created ? Results.Created("/api/contributions/" + result.Receipt.Id, result.Receipt) : Results.Ok(result.Receipt);
        }).RequireRateLimiting("create").WithTags("Contributions").Produces<ContributionReceipt>(201).Produces<ContributionReceipt>(200);
        app.MapGet("/api/contributions/{id}", async (string id, HttpContext http, ContributionService service, CancellationToken ct) =>
            Results.Ok(ContributionDto.From(await service.AuthorizeAsync(ParseId(id), Header(http, "X-Contribution-Token"), ct))))
            .RequireRateLimiting("private").WithTags("Contributions").Produces<ContributionDto>();
        app.MapPost("/api/contributions/{id}/checkout", async (string id, EmptyInput input, HttpContext http, CheckoutService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(ParseId(id), Header(http, "X-Contribution-Token"), Header(http, "Idempotency-Key"), ct)))
            .RequireRateLimiting("checkout").WithTags("Contributions").Produces<CheckoutResult>();

        var admin = app.MapGroup("/api/admin").RequireAuthorization("Couple").WithTags("Administration");
        admin.MapGet("/gifts", async (WeddingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Gifts.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct)).Select(GiftDto.From).ToArray())).Produces<GiftDto[]>();
        admin.MapPost("/gifts", async (GiftInput input, WeddingDbContext db, CancellationToken ct) =>
        {
            input = Rules.Normalize(input);
            var gift = new Gift(); Assign(gift, input); db.Gifts.Add(gift); await db.SaveChangesAsync(ct);
            return Results.Created("/api/admin/gifts/" + gift.Id, GiftDto.From(gift));
        }).Produces<GiftDto>(201);
        admin.MapPut("/gifts/{id}", async (string id, GiftInput input, WeddingDbContext db, CancellationToken ct) =>
        {
            input = Rules.Normalize(input);
            var gift = await db.Gifts.SingleOrDefaultAsync(x => x.Id == ParseId(id), ct) ?? throw ApiException.Missing();
            Assign(gift, input); await db.SaveChangesAsync(ct); return Results.Ok(GiftDto.From(gift));
        }).Produces<GiftDto>();
        admin.MapGet("/contributions", async (HttpContext http, WeddingDbContext db, CancellationToken ct) =>
        {
            var page = QueryNumber(http, "page", 1); var size = QueryNumber(http, "pageSize", 10);
            if (page < 1 || size is < 1 or > 100 || (long)(page - 1) * size > int.MaxValue) throw new ApiException(400, "invalid_pagination", "Paginação inválida.");
            var status = http.Request.Query["status"].ToString();
            if (http.Request.Query.ContainsKey("status") && !PaymentStates.All.Contains(status)) throw new ApiException(400, "invalid_status", "Estado inválido.");
            var query = db.Contributions.AsNoTracking().AsQueryable();
            if (status.Length > 0) query = query.Where(x => x.Status == status);
            var total = await query.LongCountAsync(ct);
            var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new ContributionPage(items.Select(ContributionDto.From).ToArray(), page, size, total));
        }).Produces<ContributionPage>();
        admin.MapGet("/summary", async (WeddingDbContext db, CancellationToken ct) =>
        {
            var totals = await db.Contributions.Where(x => x.Status == "approved").GroupBy(x => 1).Select(g => new
            { Count = g.LongCount(), Gross = g.Sum(x => x.AmountCentsSnapshot), Net = g.Sum(x => x.NetAmountCents), Unknown = g.Any(x => x.NetAmountCents == null) }).SingleOrDefaultAsync(ct);
            return Results.Ok(totals is null ? new GiftSummary(0, 0, 0) : new GiftSummary(totals.Count, totals.Gross, totals.Unknown ? null : totals.Net));
        }).Produces<GiftSummary>();
        app.MapPost("/api/webhooks/mercadopago", async (HttpContext http, WebhookService webhooks, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            await webhooks.ReceiveAsync(body, Header(http, "x-signature"), Header(http, "x-request-id"), http.Request.Query["data.id"].Count == 1 ? http.Request.Query["data.id"].ToString() : null, ct);
            return Results.Ok();
        }).WithTags("Webhooks");
    }
    private static Guid ParseId(string value) => Guid.TryParse(value, out var id) ? id : throw new ApiException(400, "invalid_id", "Identificador inválido.");
    private static int QueryNumber(HttpContext http, string key, int fallback) => !http.Request.Query.ContainsKey(key) ? fallback :
        http.Request.Query[key].Count == 1 && int.TryParse(http.Request.Query[key], out var value) ? value : throw new ApiException(400, "invalid_query", "Parâmetro inválido.");
    private static string? Header(HttpContext http, string key) => http.Request.Headers[key].Count switch
    { 0 => null, 1 => http.Request.Headers[key].ToString(), _ => throw new ApiException(400, "invalid_header", "Cabeçalho inválido.") };
    private static void Assign(Gift gift, GiftInput input)
    {
        gift.Title = input.Title; gift.Description = input.Description; gift.ImageUrl = input.ImageUrl;
        gift.AmountCents = input.AmountCents; gift.Active = input.Active; gift.UpdatedAt = DateTimeOffset.UtcNow;
    }
    private static class DummyPassword
    {
        public static readonly string Hash = new PasswordHasher<CoupleUser>().HashPassword(new CoupleUser(), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    }
}
