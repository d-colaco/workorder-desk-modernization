using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Sync.Tests;

/// <summary>A real SQL Server 2022 with Microsoft's AdventureWorks2022 restored and the lab's own legacy/sql
/// scripts applied, exactly as `make up` does. <see cref="ResetAsync"/> restores a pristine copy (about a
/// second) so every test starts from the same database. Needs data/AdventureWorks2022.bak (`make setup`).</summary>
public class SqlServerFixture : IAsyncLifetime
{
    public const string Database = "AdventureWorks2022";
    public const string AppPassword = "Test_App_Passw0rd!";

    private readonly MsSqlContainer _sql;

    public SqlServerFixture()
    {
        var bak = Path.Combine(RepoRoot(), "data", "AdventureWorks2022.bak");
        if (!File.Exists(bak)) throw new FileNotFoundException("run `make setup` first", bak);
        _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithBindMount(bak, "/backup/AdventureWorks2022.bak")
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
            .Build();
    }

    public Task InitializeAsync() => _sql.StartAsync();
    public Task DisposeAsync() => _sql.DisposeAsync().AsTask();

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "legacy", "sql"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    private SqlConnectionStringBuilder Builder(string db, string user, string password) =>
        new(_sql.GetConnectionString()) { InitialCatalog = db, UserID = user, Password = password, TrustServerCertificate = true, Pooling = true };

    public string AdminConnectionString(string db = Database) => Builder(db, "sa", "yourStrong(!)Password").ConnectionString;

    /// <summary>What the relay and the command consumer use: the lab_app login, never sa.</summary>
    public string AppConnectionString => Builder(Database, "lab_app", AppPassword).ConnectionString;

    /// <param name="patchTrigger">False leaves Microsoft's uWorkOrder as shipped (it cannot change an order quantity).</param>
    public async Task ResetAsync(bool patchTrigger = true)
    {
        SqlConnection.ClearAllPools();
        await using (var master = new SqlConnection(AdminConnectionString("master")))
        {
            await master.OpenAsync();
            await Run(master, $"""
                IF DB_ID('{Database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{Database}];
                END
                RESTORE DATABASE [{Database}] FROM DISK = '/backup/AdventureWorks2022.bak'
                  WITH MOVE 'AdventureWorks2022' TO '/var/opt/mssql/data/aw.mdf',
                       MOVE 'AdventureWorks2022_log' TO '/var/opt/mssql/data/aw_log.ldf', REPLACE;
                """);
        }
        await using var db = new SqlConnection(AdminConnectionString());
        await db.OpenAsync();
        foreach (var script in Directory.GetFiles(Path.Combine(RepoRoot(), "legacy", "sql"), "V*.sql").Order())
        {
            if (!patchTrigger && Path.GetFileName(script).StartsWith("V006")) continue;
            await RunScript(db, script);
        }
    }

    /// <summary>Applies one legacy/sql script by its file prefix, e.g. "V006" (used to ship the lab patch mid-test).</summary>
    public async Task ApplyScriptAsync(string prefix)
    {
        await using var db = new SqlConnection(AdminConnectionString());
        await db.OpenAsync();
        await RunScript(db, Directory.GetFiles(Path.Combine(RepoRoot(), "legacy", "sql"), prefix + "*.sql").Single());
    }

    private static async Task RunScript(SqlConnection db, string script)
    {
        var text = (await File.ReadAllTextAsync(script)).Replace("$(LabAppPassword)", AppPassword);
        foreach (var batch in Regex.Split(text, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await Run(db, batch);
    }

    private static async Task Run(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string, object)[] ps)
    {
        await using var c = new SqlConnection(AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var c = new SqlConnection(AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task ExecAsync(string sql)
    {
        await using var c = new SqlConnection(AdminConnectionString());
        await c.OpenAsync();
        await Run(c, sql);
    }

    /// <summary>Calls a legacy procedure as the desktop client would: the lab_app login, no sync markers.</summary>
    public async Task DesktopAsync(string sql)
    {
        await using var c = new SqlConnection(AppConnectionString);
        await c.OpenAsync();
        await Run(c, sql);
    }
}
