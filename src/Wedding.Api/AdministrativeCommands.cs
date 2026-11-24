using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wedding.Infrastructure;

namespace Wedding.Api;

public static class AdministrativeCommands
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, string[] args)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        try
        {
            if (args is ["db", "migrate"])
            {
                var direct = config.GetConnectionString("Migrations") ?? throw new InvalidOperationException("Direct migrations connection is required.");
                var environment = services.GetRequiredService<IHostEnvironment>();
                db.Database.SetConnectionString(DatabaseConnections.Normalize(direct, !environment.IsDevelopment() && !environment.IsEnvironment("Testing"), migrations: true));
                await db.Database.MigrateAsync(); return 0;
            }
            if (args.Length >= 2 && args[0] == "users")
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<CoupleUser>>();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
                if (args is ["users", "create", var email, var name])
                {
                    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                    if (await users.Users.CountAsync() >= 2) throw new InvalidOperationException("This application supports two couple accounts.");
                    if (!await roles.RoleExistsAsync("Couple")) Check(await roles.CreateAsync(new IdentityRole<Guid>("Couple")));
                    var user = new CoupleUser { Id = Guid.NewGuid(), Email = email.Trim(), UserName = email.Trim(), Name = name.Trim(), LockoutEnabled = true };
                    if (user.Name.Length is < 2 or > 100) throw new InvalidOperationException("Name must contain 2–100 characters.");
                    Check(await users.CreateAsync(user, ReadPassword())); Check(await users.AddToRoleAsync(user, "Couple"));
                    await tx.CommitAsync(); return 0;
                }
                if (args is ["users", "reset-password", var address])
                {
                    var user = await users.FindByEmailAsync(address) ?? throw new InvalidOperationException("Account not found.");
                    var password = ReadPassword();
                    await using var tx = await db.Database.BeginTransactionAsync();
                    Check(await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password));
                    await db.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(); await tx.CommitAsync(); return 0;
                }
            }
            if (args.Length >= 2 && args[0] == "jobs")
            {
                var processor = scope.ServiceProvider.GetRequiredService<DurableProcessor>();
                if (args is ["jobs", "reprocess", var eventId])
                {
                    var id = Guid.Parse(eventId);
                    var count = await db.WebhookInbox.Where(x => x.Id == id && (x.LeaseUntil == null || x.LeaseUntil < DateTimeOffset.UtcNow)).ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ProcessedAt, (DateTimeOffset?)null).SetProperty(x => x.Attempts, 0).SetProperty(x => x.ManualIntervention, false)
                        .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow).SetProperty(x => x.LeaseOwner, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null));
                    return count == 1 ? 0 : 1;
                }
                if (args[1] == "reconcile")
                {
                    Guid? id = null; DateTimeOffset? from = null, to = null; string? orderId = null;
                    for (var i = 2; i < args.Length; i += 2)
                    {
                        if (i + 1 >= args.Length) throw new InvalidOperationException("Missing option value.");
                        switch (args[i])
                        {
                            case "--id": id = Guid.Parse(args[i + 1]); break;
                            case "--order-id": orderId = args[i + 1]; break;
                            case "--from": from = DateTimeOffset.Parse(args[i + 1], CultureInfo.InvariantCulture).ToUniversalTime(); break;
                            case "--to": to = DateTimeOffset.Parse(args[i + 1], CultureInfo.InvariantCulture).ToUniversalTime(); break;
                            default: throw new InvalidOperationException("Unknown reconcile option.");
                        }
                    }
                    if (from > to) throw new InvalidOperationException("Invalid period.");
                    if (orderId is not null)
                    {
                        if (id is null) throw new InvalidOperationException("--order-id requires --id.");
                        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.ContributionId == id);
                        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var order = await scope.ServiceProvider.GetRequiredService<Wedding.Application.IMercadoPagoClient>().GetOrderAsync(orderId, budget.Token);
                        if (order.Reference != attempt.Id.ToString()) throw new InvalidOperationException("Order reference does not match this attempt.");
                        await scope.ServiceProvider.GetRequiredService<FinancialSync>().ApplyAsync(order, CancellationToken.None);
                    }
                    if (id is not null || from is not null || to is not null)
                    {
                        var query = db.PaymentAttempts.Where(x => x.LeaseUntil == null || x.LeaseUntil < DateTimeOffset.UtcNow);
                        if (id is { } contributionId) query = query.Where(x => x.ContributionId == contributionId);
                        if (from is { } start) query = query.Where(x => x.CreatedAt >= start);
                        if (to is { } end) query = query.Where(x => x.CreatedAt <= end);
                        // Explicit targeting schedules existing known orders; it never invents a new provider key.
                        await query.Where(x => x.ProviderOrderId != null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ManualIntervention, false).SetProperty(x => x.SyncAttempts, 0).SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow));
                    }
                    for (var i = 0; i < 1000 && await processor.ProcessInboxAsync(CancellationToken.None); i++) { }
                    for (var i = 0; i < 1000 && await processor.ProcessAttemptAsync(id, from, to, CancellationToken.None); i++) { }
                    return 0;
                }
            }
            Console.Error.WriteLine("Commands: db migrate | users create <email> <name> | users reset-password <email> | jobs reconcile [--id UUID [--order-id provider-id]] [--from ISO] [--to ISO] | jobs reprocess <inbox UUID>");
            return 2;
        }
        catch (Exception ex)
        {
            // Do not print provider responses, connection strings, secrets, or EF exceptions.
            Console.Error.WriteLine(ex is InvalidOperationException ? ex.Message : "Administrative command failed. Inspect sanitized operational logs."); return 1;
        }
    }
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Identity operation failed: " + string.Join(", ", result.Errors.Select(x => x.Code)));
    }
    private static string ReadPassword()
    {
        var secret = Environment.GetEnvironmentVariable("WEDDING_TEMP_PASSWORD");
        Environment.SetEnvironmentVariable("WEDDING_TEMP_PASSWORD", null);
        if (!string.IsNullOrEmpty(secret)) return secret;
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use an interactive terminal or WEDDING_TEMP_PASSWORD temporary secret.");
        Console.Write("Password: "); var value = new System.Text.StringBuilder();
        while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; } else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar); }
        Console.WriteLine(); return value.ToString();
    }
}
