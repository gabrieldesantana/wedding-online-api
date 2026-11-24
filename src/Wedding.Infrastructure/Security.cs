using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Wedding.Application;

namespace Wedding.Infrastructure;

public sealed class TrackingTokens(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Wedding.ContributionTracking.v1");
    public (string Token, byte[] Hash, string Protected) Create()
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (token, SHA256.HashData(Encoding.UTF8.GetBytes(token)), protector.Protect(token));
    }
    public string Recover(string value) => protector.Unprotect(value);
    public bool Verify(string? token, byte[] expected)
    {
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""));
        return token?.Length == 43 && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
public sealed class DatabaseTicketStore(IServiceScopeFactory scopes, IDataProtectionProvider protection) : ITicketStore
{
    private readonly IDataProtector protector = protection.CreateProtector("Wedding.SessionTicket.v1");
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        db.Sessions.Add(new StoredSession { Id = key, UserId = Guid.Parse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Ticket = protector.Protect(TicketSerializer.Default.Serialize(ticket)), ExpiresAt = ticket.Properties.ExpiresUtc!.Value });
        await db.SaveChangesAsync();
        return key;
    }
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        // Never recreate a revoked session, and never extend its absolute expiration.
        await db.Sessions.Where(x => x.Id == key && x.ExpiresAt > DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Ticket, protector.Protect(TicketSerializer.Default.Serialize(ticket))));
    }
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WeddingDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == key && x.ExpiresAt > DateTimeOffset.UtcNow);
        if (session is null) return null;
        var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(session.Ticket));
        if (ticket is null) return null;
        var stamp = await db.Users.Where(x => x.Id == session.UserId).Select(x => x.SecurityStamp).SingleOrDefaultAsync();
        return stamp is not null && stamp == ticket.Principal.FindFirstValue("AspNet.Identity.SecurityStamp") ? ticket : null;
    }
    public async Task RemoveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WeddingDbContext>().Sessions.Where(x => x.Id == key).ExecuteDeleteAsync();
    }
}
