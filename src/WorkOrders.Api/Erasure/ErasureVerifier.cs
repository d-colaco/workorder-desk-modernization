using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Sync.Contracts;

namespace WorkOrders.Api.Erasure;

/// <summary>One place where a copy of the subject was found. Reports the kind of identifier, never its value, so the
/// check does not itself put personal data into a log.</summary>
public record Hit(string Store, string Where, string Kind, long Count);

/// <summary>Looks for the subject everywhere the lab stores or moves data: every text, JSON and XML column of both
/// databases, and the headers and bodies of every message in the broker. Used both before an erasure (to prove the
/// search can find something) and after (to prove nothing is left).</summary>
public class ErasureVerifier(string postgresConnectionString, string legacyConnectionString, IDeadLetterAdmin broker)
{
    private static readonly string[] AllQueues =
    [
        Topology.NewSideQueue, Topology.LegacyQueue, Topology.NewSideDeadLetterQueue, Topology.LegacyDeadLetterQueue,
        "newside.workorders.retry", "legacy.commands.retry",
    ];

    public async Task<List<Hit>> ScanAsync(IReadOnlyCollection<Needle> needles, int subject, CancellationToken ct = default)
    {
        var hits = new List<Hit>();
        hits.AddRange(await ScanPostgresAsync(needles, subject, ct));
        hits.AddRange(await ScanSqlServerAsync(needles, subject, ct));
        hits.AddRange(await ScanBrokerAsync(needles, subject, ct));
        return hits;
    }

    private static string Like(string value) => "%" + Regex.Replace(value, @"([\\%_\[])", @"\$1") + "%";

    private async Task<List<Hit>> ScanPostgresAsync(IReadOnlyCollection<Needle> needles, int subject, CancellationToken ct)
    {
        var hits = new List<Hit>();
        await using var c = new NpgsqlConnection(postgresConnectionString);
        await c.OpenAsync(ct);
        var columns = new List<(string T, string C)>();
        await using (var cmd = new NpgsqlCommand(@"SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
              AND data_type IN ('text', 'character varying', 'character', 'json', 'jsonb')", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) columns.Add((r.GetString(0), r.GetString(1)));

        foreach (var (t, col) in columns)
            foreach (var n in needles)
            {
                await using var q = new NpgsqlCommand($"SELECT count(*) FROM \"{t}\" WHERE \"{col}\"::text ILIKE @n", c) { Parameters = { new("n", Like(n.Value)) } };
                var count = (long)(await q.ExecuteScalarAsync(ct))!;
                if (count > 0) hits.Add(new("postgres", $"{t}.{col}", n.Kind, count));
            }

        // The actor reference inside command payloads.
        await using (var q = new NpgsqlCommand("SELECT count(*) FROM outbox WHERE (payload->>'actorBusinessEntityId') = @s", c) { Parameters = { new("s", subject.ToString()) } })
        {
            var count = (long)(await q.ExecuteScalarAsync(ct))!;
            if (count > 0) hits.Add(new("postgres", "outbox.payload.actorBusinessEntityId", "actor reference", count));
        }
        await using (var q = new NpgsqlCommand("SELECT count(*) FROM planner WHERE business_entity_id = @s AND (display_name IS NOT NULL OR erased_at IS NULL)", c) { Parameters = { new("s", subject) } })
        {
            var count = (long)(await q.ExecuteScalarAsync(ct))!;
            if (count > 0) hits.Add(new("postgres", "planner", "planner still active or labelled", count));
        }
        return hits;
    }

    private async Task<List<Hit>> ScanSqlServerAsync(IReadOnlyCollection<Needle> needles, int subject, CancellationToken ct)
    {
        var hits = new List<Hit>();
        await using var c = new SqlConnection(legacyConnectionString);
        await c.OpenAsync(ct);
        var tables = new Dictionary<string, List<string>>();
        // char, varchar, text, nchar, nvarchar, ntext, xml (alias types such as Name and Phone resolve to these)
        await using (var cmd = new SqlCommand(@"SELECT s.name + '.' + t.name, c.name FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE c.system_type_id IN (167, 175, 231, 239, 35, 99, 241) AND t.is_ms_shipped = 0", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!tables.TryGetValue(r.GetString(0), out var l)) tables[r.GetString(0)] = l = new();
                l.Add(r.GetString(1));
            }

        // One pass over each table counts every needle at once.
        var list = needles.ToList();
        foreach (var (table, cols) in tables)
        {
            var sums = string.Join(", ", list.Select((_, i) =>
                $"SUM(CASE WHEN {string.Join(" OR ", cols.Select(col => $"CAST([{col}] AS nvarchar(max)) LIKE @n{i} ESCAPE '\\'"))} THEN 1 ELSE 0 END)"));
            await using var q = new SqlCommand($"SELECT {sums} FROM {table}", c) { CommandTimeout = 300 };
            for (var i = 0; i < list.Count; i++) q.Parameters.AddWithValue("@n" + i, Like(list[i].Value));
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                for (var i = 0; i < list.Count; i++)
                    if (!r.IsDBNull(i) && Convert.ToInt64(r.GetValue(i)) is > 0 and var count) hits.Add(new("sqlserver", table, list[i].Kind, count));
        }

        await using (var q = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.WorkOrderAudit WHERE ActorBusinessEntityID = @s", c))
        {
            q.Parameters.AddWithValue("@s", subject);
            var count = (long)(await q.ExecuteScalarAsync(ct))!;
            if (count > 0) hits.Add(new("sqlserver", "dbo.WorkOrderAudit.ActorBusinessEntityID", "actor reference", count));
        }
        return hits;
    }

    private async Task<List<Hit>> ScanBrokerAsync(IReadOnlyCollection<Needle> needles, int subject, CancellationToken ct)
    {
        var hits = new List<Hit>();
        var actor = new Regex($"\"actorBusinessEntityId\"\\s*:\\s*{subject}\\b");
        foreach (var m in await broker.PeekTextAsync(AllQueues, ct))
        {
            foreach (var n in needles)
                if (m.Text.Contains(n.Value, StringComparison.OrdinalIgnoreCase)) hits.Add(new("broker", m.Queue, n.Kind, 1));
            if (actor.IsMatch(m.Text)) hits.Add(new("broker", m.Queue, "actor reference", 1));
        }
        return hits;
    }
}
