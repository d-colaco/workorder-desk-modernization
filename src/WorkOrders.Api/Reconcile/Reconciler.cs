using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Data;

namespace WorkOrders.Api.Reconcile;

public enum DifferenceClass
{
    /// <summary>A change is on its way: unpublished outbox row, unconfirmed command, or a legacy version not yet applied.</summary>
    InFlight,
    /// <summary>A message about this order is sitting in a dead-letter queue. Needs a person: fix the cause, then re-drive.</summary>
    DeadLettered,
    /// <summary>Nothing in flight and nothing dead-lettered, yet the sides differ. This should never be non-zero.</summary>
    Unexplained,
}

public record OrderDifference(int? LegacyWorkOrderId, Guid? NewId, IReadOnlyList<string> Fields, DifferenceClass Class, string Reason);

public record ConflictRow(string Side, int WorkOrderId, string Field, string? LegacyValue, string? NewValue, string Winner, DateTime At);

public record ReconciliationReport(DateTime At, int Compared, int Equal, IReadOnlyList<OrderDifference> Differences,
    IReadOnlyList<ConflictRow> Conflicts, IReadOnlyList<string> Warnings, IReadOnlyList<DeadLetter> DeadLetters)
{
    public int Unexplained => Differences.Count(d => d.Class == DifferenceClass.Unexplained);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Reconciliation report, {At:yyyy-MM-dd HH:mm:ss} UTC").AppendLine();
        sb.AppendLine($"- Compared: {Compared} work orders; identical: {Equal}; different: {Differences.Count}");
        foreach (var g in Differences.GroupBy(d => d.Class).OrderBy(g => g.Key)) sb.AppendLine($"  - {g.Key}: {g.Count()}");
        sb.AppendLine($"- **Unexplained: {Unexplained}**");
        sb.AppendLine($"- Messages in dead-letter queues: {DeadLetters.Count}" + (DeadLetters.Count > 0 ? " (see below; fix the cause, then `make redrive`)" : ""));
        foreach (var w in Warnings) sb.AppendLine($"- Warning: {w}");
        if (Differences.Count > 0)
        {
            sb.AppendLine().AppendLine("## Differences").AppendLine().AppendLine("| Order | Class | Differing fields | Why |").AppendLine("|---|---|---|---|");
            foreach (var d in Differences.OrderByDescending(d => d.Class == DifferenceClass.Unexplained).ThenBy(d => d.LegacyWorkOrderId))
                sb.AppendLine($"| {(d.LegacyWorkOrderId?.ToString() ?? "pending " + d.NewId)} | {d.Class} | {string.Join(", ", d.Fields)} | {d.Reason} |");
        }
        if (DeadLetters.Count > 0)
        {
            sb.AppendLine().AppendLine("## Dead-lettered messages").AppendLine().AppendLine("| Queue | Message | Type | Order | Attempts | Error |").AppendLine("|---|---|---|---|---|---|");
            foreach (var d in DeadLetters)
                sb.AppendLine($"| {d.Queue} | {d.MessageId} | {d.Type} | {(d.AggregateId == 0 ? "create " + d.NewId : d.AggregateId.ToString())} | {d.Attempts} | {d.Error} |");
        }
        if (Conflicts.Count > 0)
        {
            sb.AppendLine().AppendLine("## Conflicts resolved (informational)").AppendLine()
              .AppendLine("| Side | Order | Field | Legacy value (server time, no zone) | New value | Winner | When (UTC) |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var c in Conflicts.OrderByDescending(c => c.At).Take(50))
                sb.AppendLine($"| {c.Side} | {c.WorkOrderId} | {c.Field} | {c.LegacyValue} | {c.NewValue} | {c.Winner} | {c.At:HH:mm:ss} |");
        }
        return sb.ToString();
    }
}

/// <summary>Compares every work order the new side holds (plus every legacy order that has changed since syncing
/// began) field by field, and explains each difference. The sides are allowed to differ only while a change is in
/// flight; anything else is reported as unexplained.
///
/// The databases and the broker are read one after another, so a change that commits between two reads can look
/// unexplained for an instant. An order is therefore reported as unexplained only if it is still unexplained when
/// everything is read again <paramref name="recheckAfter"/> later (default 2 s, several relay polls); the ones that
/// cleared are named in a warning. <paramref name="beforeRecheck"/> is a seam for tests.</summary>
public class Reconciler(LabDbContext db, string legacyConnectionString, IDeadLetterAdmin? deadLetters = null,
    TimeSpan? recheckAfter = null, Func<CancellationToken, Task>? beforeRecheck = null)
{
    public async Task<ReconciliationReport> RunAsync(CancellationToken ct = default)
    {
        var first = await PassAsync(ct);
        if (first.Unexplained == 0) return first;

        if (beforeRecheck != null) await beforeRecheck(ct);
        await Task.Delay(recheckAfter ?? TimeSpan.FromSeconds(2), ct);
        var second = await PassAsync(ct);

        static string Key(OrderDifference d) => d.LegacyWorkOrderId?.ToString() ?? "new " + d.NewId;
        var still = second.Differences.Where(d => d.Class == DifferenceClass.Unexplained).Select(Key).ToHashSet();
        var cleared = first.Differences.Where(d => d.Class == DifferenceClass.Unexplained).Select(Key).Where(k => !still.Contains(k)).ToList();
        if (cleared.Count == 0) return second;
        var note = $"{cleared.Count} order(s) were unexplained on the first read and cleared on the second, a change was crossing the databases: {string.Join(", ", cleared)}";
        return second with { Warnings = [.. second.Warnings, note] };
    }

    private async Task<ReconciliationReport> PassAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        var mine = await db.WorkOrders.AsNoTracking().Include(w => w.Operations).ToListAsync(ct);
        var unconfirmed = await db.Outbox.AsNoTracking().Where(o => o.ConfirmedAt == null).ToListAsync(ct);

        await using var conn = new SqlConnection(legacyConnectionString);
        await conn.OpenAsync(ct);
        var touched = await ReadVersions(conn, ct);   // legacy id -> version, for orders changed since syncing began
        var unpublished = await ReadSet(conn, "SELECT DISTINCT AggregateId FROM sync.Outbox WHERE PublishedAt IS NULL", ct);

        var ids = mine.Where(w => w.LegacyWorkOrderId != null).Select(w => w.LegacyWorkOrderId!.Value).Union(touched.Keys).Distinct().ToList();
        var legacy = await ReadLegacy(conn, ids, ct);

        var dead = new List<DeadLetter>();
        if (deadLetters != null)
        {
            try
            {
                dead.AddRange(await deadLetters.PeekAsync(Topology.NewSideDeadLetterQueue, ct));
                dead.AddRange(await deadLetters.PeekAsync(Topology.LegacyDeadLetterQueue, ct));
            }
            catch (Exception ex) { warnings.Add("dead-letter queues could not be inspected (" + ex.Message + "); dead-lettered messages may show as unexplained or in flight"); }
        }

        var diffs = new List<OrderDifference>();
        var equal = 0;
        var compared = 0;

        DeadLetter? DeadFor(int? legacyId, Guid? newId, IEnumerable<OutboxMessage> commands)
        {
            var commandIds = commands.Select(c => $"new-{c.Id}").ToHashSet();
            return dead.FirstOrDefault(d => (legacyId != null && d.AggregateId == legacyId && d.AggregateId != 0)
                || (newId != null && d.NewId == newId.ToString())
                || (d.CommandId != null && commandIds.Contains(d.CommandId)));
        }

        foreach (var w in mine)
        {
            compared++;
            var commands = unconfirmed.Where(o => (w.LegacyWorkOrderId != null && o.LegacyWorkOrderId == w.LegacyWorkOrderId) || (o.NewId == w.Id)).ToList();
            if (w.LegacyWorkOrderId is not { } lid)
            {
                var dl = DeadFor(null, w.Id, commands);
                if (dl != null) diffs.Add(new(null, w.Id, ["legacy id"], DifferenceClass.DeadLettered, $"{dl.Type} {dl.MessageId} in {dl.Queue}: {dl.Error}"));
                else if (commands.Count > 0) diffs.Add(new(null, w.Id, ["legacy id"], DifferenceClass.InFlight, "create command not confirmed yet"));
                else diffs.Add(new(null, w.Id, ["legacy id"], DifferenceClass.Unexplained, "create was confirmed but no legacy id was assigned"));
                continue;
            }

            if (!legacy.TryGetValue(lid, out var l))
            {
                diffs.Add(Classify(lid, w, ["(missing on legacy side)"], touched, unpublished, commands, DeadFor(lid, w.Id, commands)));
                continue;
            }
            var fields = Diff(Canon.From(w), l);
            if (fields.Count == 0) { equal++; continue; }
            diffs.Add(Classify(lid, w, fields, touched, unpublished, commands, DeadFor(lid, w.Id, commands)));
        }

        var mineIds = mine.Where(w => w.LegacyWorkOrderId != null).Select(w => w.LegacyWorkOrderId!.Value).ToHashSet();
        foreach (var lid in touched.Keys.Where(k => !mineIds.Contains(k) && legacy.ContainsKey(k)))
        {
            compared++;
            diffs.Add(Classify(lid, null, ["(missing on new side)"], touched, unpublished, [], DeadFor(lid, null, [])));
        }

        var conflicts = await ReadConflicts(conn, ct);
        return new ReconciliationReport(DateTime.UtcNow, compared, equal, diffs, conflicts, warnings, dead);
    }

    private OrderDifference Classify(int legacyId, WorkOrder? mine, IReadOnlyList<string> fields, Dictionary<int, long> legacyVersions,
        HashSet<int> unpublished, List<OutboxMessage> commands, DeadLetter? dead)
    {
        if (dead != null)
            return new(legacyId, mine?.Id, fields, DifferenceClass.DeadLettered, $"{dead.Type} {dead.MessageId} in {dead.Queue}" + (dead.Error != null ? $": {dead.Error}" : ""));
        var why = new List<string>();
        if (unpublished.Contains(legacyId)) why.Add("legacy outbox has unpublished changes");
        if (commands.Count > 0) why.Add($"{commands.Count} command(s) not confirmed yet");
        if (legacyVersions.TryGetValue(legacyId, out var lv) && lv > (mine?.LegacyVersion ?? 0)) why.Add($"legacy is at version {lv}, new side has applied {(mine?.LegacyVersion ?? 0)}");
        return why.Count > 0
            ? new(legacyId, mine?.Id, fields, DifferenceClass.InFlight, string.Join("; ", why))
            : new(legacyId, mine?.Id, fields, DifferenceClass.Unexplained, "nothing in flight, nothing dead-lettered");
    }

    // ----- canonical form: the same fields on both sides, with timestamps and decimals normalised -----

    private record Op(short Seq, short Location, DateTime SchedStart, DateTime SchedEnd, DateTime? ActStart, DateTime? ActEnd, decimal? Hrs, decimal Planned, decimal? Cost);

    private record Canon(int ProductId, int Qty, int Scrapped, int? ScrapReason, DateTime Start, DateTime Due, DateTime? End, List<Op> Ops)
    {
        public static Canon From(WorkOrder w) => new(w.LegacyProductId, w.OrderQty, w.ScrappedQty, w.ScrapReasonId, Ms(w.StartDate), Ms(w.DueDate), Ms(w.EndDate),
            w.Operations.OrderBy(o => o.OperationSequence).Select(o => new Op(o.OperationSequence, o.LocationId, Ms(o.ScheduledStartDate), Ms(o.ScheduledEndDate),
                Ms(o.ActualStartDate), Ms(o.ActualEndDate), Dec(o.ActualResourceHrs), Dec(o.PlannedCost), Dec(o.ActualCost))).ToList());
    }

    private static DateTime Ms(DateTime d) => new(d.Ticks - d.Ticks % TimeSpan.TicksPerMillisecond);
    private static DateTime? Ms(DateTime? d) => d is null ? null : Ms(d.Value);
    private static decimal Dec(decimal d) => Math.Round(d, 4);
    private static decimal? Dec(decimal? d) => d is null ? null : Math.Round(d.Value, 4);

    private static List<string> Diff(Canon a, Canon b)
    {
        var f = new List<string>();
        if (a.ProductId != b.ProductId) f.Add("product");
        if (a.Qty != b.Qty) f.Add($"orderQty ({a.Qty} vs {b.Qty})");
        if (a.Scrapped != b.Scrapped) f.Add($"scrappedQty ({a.Scrapped} vs {b.Scrapped})");
        if (a.ScrapReason != b.ScrapReason) f.Add("scrapReason");
        if (a.Start != b.Start) f.Add("startDate");
        if (a.Due != b.Due) f.Add($"dueDate ({a.Due:yyyy-MM-dd} vs {b.Due:yyyy-MM-dd})");
        if (a.End != b.End) f.Add("endDate");
        if (a.Ops.Count != b.Ops.Count) f.Add($"operations ({a.Ops.Count} vs {b.Ops.Count})");
        else foreach (var (x, y) in a.Ops.Zip(b.Ops)) if (x != y) f.Add($"operation {x.Seq}");
        return f;
    }

    // ----- legacy reads -----

    private static async Task<Dictionary<int, long>> ReadVersions(SqlConnection c, CancellationToken ct)
    {
        var d = new Dictionary<int, long>();
        await using var cmd = new SqlCommand("SELECT AggregateId, Version FROM sync.AggregateVersion", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetInt32(0)] = r.GetInt64(1);
        return d;
    }

    private static async Task<HashSet<int>> ReadSet(SqlConnection c, string sql, CancellationToken ct)
    {
        var s = new HashSet<int>();
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) s.Add(r.GetInt32(0));
        return s;
    }

    private static async Task<Dictionary<int, Canon>> ReadLegacy(SqlConnection c, List<int> ids, CancellationToken ct)
    {
        var result = new Dictionary<int, Canon>();
        var ops = new Dictionary<int, List<Op>>();
        foreach (var chunk in ids.Chunk(500))
        {
            var list = string.Join(",", chunk);
            await using (var cmd = new SqlCommand($@"SELECT WorkOrderID, OperationSequence, LocationID, ScheduledStartDate, ScheduledEndDate, ActualStartDate,
                ActualEndDate, ActualResourceHrs, PlannedCost, ActualCost FROM Production.WorkOrderRouting WHERE WorkOrderID IN ({list})", c))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetInt32(0);
                    if (!ops.TryGetValue(id, out var l)) ops[id] = l = new();
                    l.Add(new Op(r.GetInt16(1), r.GetInt16(2), Ms(r.GetDateTime(3)), Ms(r.GetDateTime(4)), Ms(r.IsDBNull(5) ? null : r.GetDateTime(5)),
                        Ms(r.IsDBNull(6) ? null : r.GetDateTime(6)), Dec(r.IsDBNull(7) ? null : r.GetDecimal(7)), Dec(r.GetDecimal(8)), Dec(r.IsDBNull(9) ? null : r.GetDecimal(9))));
                }
            await using (var cmd = new SqlCommand($@"SELECT WorkOrderID, ProductID, OrderQty, ScrappedQty, ScrapReasonID, StartDate, DueDate, EndDate
                FROM Production.WorkOrder WHERE WorkOrderID IN ({list})", c))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetInt32(0);
                    result[id] = new Canon(r.GetInt32(1), r.GetInt32(2), r.GetInt16(3), r.IsDBNull(4) ? null : r.GetInt16(4), Ms(r.GetDateTime(5)), Ms(r.GetDateTime(6)),
                        Ms(r.IsDBNull(7) ? null : r.GetDateTime(7)), (ops.GetValueOrDefault(id) ?? new()).OrderBy(o => o.Seq).ToList());
                }
        }
        return result;
    }

    private async Task<List<ConflictRow>> ReadConflicts(SqlConnection c, CancellationToken ct)
    {
        var rows = new List<ConflictRow>();
        await using (var cmd = new SqlCommand("SELECT TOP (200) WorkOrderID, Field, LegacyValue, NewValue, Winner, CreatedAt FROM sync.ConflictLog ORDER BY ConflictId DESC", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add(new("legacy", r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetDateTime(5)));
        rows.AddRange((await db.Conflicts.AsNoTracking().OrderByDescending(x => x.Id).Take(200).ToListAsync(ct))
            .Select(x => new ConflictRow("new", x.LegacyWorkOrderId, x.Field, x.LegacyValue, x.NewValue, x.Winner, x.CreatedAt)));
        return rows;
    }
}
