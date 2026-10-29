using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WorkOrders.Api.Data;

namespace Sync.Tests;

/// <summary>One real PostgreSQL for the run, with the EF migrations applied. Each test gets its own context
/// and clears the tables first, so tests do not depend on each other.</summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17").Build();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    public string ConnectionString => _pg.GetConnectionString();

    public LabDbContext NewContext() => new(new DbContextOptionsBuilder<LabDbContext>()
        .UseNpgsql(_pg.GetConnectionString()).UseSnakeCaseNamingConvention().Options);

    public async Task<LabDbContext> CleanContextAsync()
    {
        var db = NewContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE work_order, work_order_operation, processed_message, outbox, conflict_log, scrap_reason, planner, erasure_log CASCADE");
        return db;
    }
}
