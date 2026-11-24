using Npgsql;

namespace Wedding.Infrastructure;

public static class DatabaseConnections
{
    public static string Normalize(string connection, bool requireTls, bool migrations = false)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (requireTls && settings.SslMode != SslMode.VerifyFull) throw new InvalidOperationException("Production database requires SSL Mode=VerifyFull.");
        if (!connection.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase) && !connection.Contains("MaxPoolSize", StringComparison.OrdinalIgnoreCase)) settings.MaxPoolSize = 10;
        settings.Timeout = 3; settings.CommandTimeout = migrations ? 60 : 3; settings.IncludeErrorDetail = false;
        // Neon/password connections use TLS, not Kerberos. Avoid optional native GSS dependencies in the Linux image.
        settings.GssEncryptionMode = GssEncryptionMode.Disable;
        return settings.ConnectionString;
    }
}
