using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wedding.Infrastructure;

namespace Wedding.Api;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WeddingDbContext>
{
    public WeddingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<WeddingDbContext>()
        .UseNpgsql("Host=localhost;Database=wedding_design;Username=design;Password=unused").Options);
}
