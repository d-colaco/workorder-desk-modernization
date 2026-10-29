using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WorkOrders.Api.Data;

/// <summary>Lets `dotnet ef` build the context without starting the web host (whose Program handles CLI commands).</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<LabDbContext>
{
    public LabDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LabDbContext>().UseNpgsql("Host=localhost;Database=design").UseSnakeCaseNamingConvention().Options);
}
