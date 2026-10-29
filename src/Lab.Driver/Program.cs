using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lab.Observability;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Sync.Contracts;

// Stands in for the WPF client on Linux (it cannot run here) and drives load. Run `edit` for one traced desktop
// edit, `bench` for a fixed-rate load whose lag numbers are read back from the databases, not from the dashboard, or
// `simulate` for synthetic background activity (whole order lifecycles, desktop and planner side by side) until stopped.
// Simulated traffic reports as service `workorder-desk-simulator` with the span tag lab.synthetic=true; never quote its rates.
//
//   edit
//   bench [--rate 5] [--seconds 60] [--api-share 0.3] [--outage-at 20 --outage-for 15] [--orders 20]
//   simulate [--per-minute 12] [--max-open 12] [--seed 0]

var synthetic = args.FirstOrDefault() == "simulate";
var services = new ServiceCollection();
services.AddLabTelemetry(synthetic ? "workorder-desk-simulator" : "workorder-desk-driver");
using var provider = services.BuildServiceProvider();
// Resolving the providers starts them (this host has no Generic Host to do it).
provider.GetService<TracerProvider>(); provider.GetService<MeterProvider>();

var legacy = Env("ConnectionStrings__LegacySql");
var postgres = Env("ConnectionStrings__Postgres");
var api = Environment.GetEnvironmentVariable("LAB_API") ?? "http://127.0.0.1:32130";
var opts = args.Skip(1).Chunk(2).Where(c => c.Length == 2 && c[0].StartsWith("--")).ToDictionary(c => c[0][2..], c => c[1]);
double Opt(string k, double d) => opts.TryGetValue(k, out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : d;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });

var code = (args.FirstOrDefault() ?? "") switch
{
    "edit" => await Edit(),
    "simulate" => await Simulate(Opt("per-minute", 12), (int)Opt("max-open", 12), (int)Opt("seed", 0), stop.Token),
    "bench" => await Bench((int)Opt("orders", 20), Opt("rate", 5), (int)Opt("seconds", 60), Opt("api-share", 0.3), (int)Opt("outage-at", 0), (int)Opt("outage-for", 0)),
    _ => Usage(),
};
provider.GetService<TracerProvider>()?.ForceFlush(5000);
provider.GetService<MeterProvider>()?.ForceFlush(5000);
return code;

static string Env(string k) => Environment.GetEnvironmentVariable(k) ?? throw new InvalidOperationException(k + " is not set");
static int Usage() { Console.Error.WriteLine("usage: Lab.Driver edit | bench [--rate N --seconds N --api-share F --outage-at S --outage-for S --orders N] | simulate [--per-minute N --max-open N --seed N]"); return 2; }

/// <summary>What the WPF client does on every call: a client span, its traceparent put in SESSION_CONTEXT on the
/// same connection, then the stored procedure. The trigger copies it into the outbox row.</summary>
async Task<object?> DesktopProc(string name, string sql, params (string, object)[] ps)
{
    using var span = LabTelemetry.Source.StartActivity("WorkOrderDesk " + name, ActivityKind.Client);
    if (synthetic) span?.SetTag("lab.synthetic", true);
    await using var c = new SqlConnection(legacy);
    await c.OpenAsync();
    await using (var ctx = new SqlCommand("EXEC sp_set_session_context N'traceparent', @tp", c))
    {
        ctx.Parameters.AddWithValue("@tp", (object?)span?.Id ?? DBNull.Value);
        await ctx.ExecuteNonQueryAsync();
    }
    await using var cmd = new SqlCommand(sql, c);
    foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
    var result = await cmd.ExecuteScalarAsync();
    if (!synthetic) Console.Error.WriteLine($"{name}: trace {span?.TraceId}");
    return result;
}

async Task<int> CreateOrder(int qty)
{
    await DesktopProc("create", "DECLARE @id int; EXEC dbo.uspWorkOrderCreate 514, @q, '2027-01-15', 27, @id OUTPUT;", ("@q", qty));
    await using var c = new SqlConnection(legacy);
    await c.OpenAsync();
    await using var m = new SqlCommand("SELECT MAX(WorkOrderID) FROM Production.WorkOrder", c);
    return (int)(await m.ExecuteScalarAsync())!;
}

async Task<int> Edit()
{
    var id = await CreateOrder(10);
    await DesktopProc("scrap", "EXEC dbo.uspWorkOrderScrap @id, 1, 7, 27", ("@id", id));
    Console.WriteLine($"order {id}: created and scrapped through the procedures with trace context in SESSION_CONTEXT");
    return 0;
}

async Task<int> Bench(int orders, double rate, int seconds, double apiShare, int outageAt, int outageFor)
{
    using var http = new HttpClient { BaseAddress = new Uri(api) };
    http.DefaultRequestHeaders.Add("X-Planner", "27");
    Console.WriteLine($"setup: {orders} orders with quantity 5000");
    var legacyIds = new List<int>();
    for (var i = 0; i < orders; i++) legacyIds.Add(await CreateOrder(5000));
    var newIds = new Dictionary<int, Guid>();
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (newIds.Count < orders && DateTime.UtcNow < deadline)
    {
        foreach (var w in (await http.GetFromJsonAsync<JsonElement>("/api/work-orders?take=200")).EnumerateArray())
            if (w.GetProperty("legacyWorkOrderId").ValueKind == JsonValueKind.Number && legacyIds.Contains(w.GetProperty("legacyWorkOrderId").GetInt32()))
                newIds[w.GetProperty("legacyWorkOrderId").GetInt32()] = w.GetProperty("id").GetGuid();
        await Task.Delay(300);
    }
    if (newIds.Count < orders) { Console.Error.WriteLine("setup orders did not reach the new side; are `make api` and `make relay` running?"); return 1; }
    await Task.Delay(1500);

    var t0 = DateTime.UtcNow;
    var interval = TimeSpan.FromSeconds(1 / rate);
    var rnd = new Random(7);
    var tasks = new List<Task>();
    int sentDesktop = 0, sentApi = 0, errors = 0;
    var sw = Stopwatch.StartNew();
    Task? outage = null;
    Console.WriteLine($"load: {rate}/s for {seconds}s, {apiShare:P0} through the API" + (outageAt > 0 ? $", broker stopped at {outageAt}s for {outageFor}s" : ""));
    for (var n = 0; sw.Elapsed.TotalSeconds < seconds; n++)
    {
        var due = TimeSpan.FromTicks(interval.Ticks * n);
        if (due > sw.Elapsed) await Task.Delay(due - sw.Elapsed);
        if (outageAt > 0 && outage == null && sw.Elapsed.TotalSeconds >= outageAt) outage = Task.Run(async () =>
        {
            Docker("stop rabbitmq"); Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F0}s] broker stopped");
            await Task.Delay(TimeSpan.FromSeconds(outageFor));
            Docker("start rabbitmq"); Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F0}s] broker started");
        });
        var order = legacyIds[rnd.Next(legacyIds.Count)];
        if (rnd.NextDouble() < apiShare)
        {
            sentApi++;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var span = LabTelemetry.Source.StartActivity("bench planner scrap", ActivityKind.Client);
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/work-orders/{newIds[order]}/scrap") { Content = JsonContent.Create(new { quantity = 1, reasonId = 7 }) };
                    if (span?.Id != null) req.Headers.Add("traceparent", span.Id);
                    using var r = await http.SendAsync(req);
                    if (!r.IsSuccessStatusCode) Interlocked.Increment(ref errors);
                }
                catch { Interlocked.Increment(ref errors); }
            }));
        }
        else
        {
            sentDesktop++;
            tasks.Add(Task.Run(async () =>
            {
                try { await DesktopProc("scrap", "EXEC dbo.uspWorkOrderScrap @id, 1, 7, 27", ("@id", order)); }
                catch { Interlocked.Increment(ref errors); }
            }));
        }
    }
    await Task.WhenAll(tasks);
    if (outage != null) await outage;
    Console.WriteLine($"sent {sentDesktop} desktop + {sentApi} API edits in {sw.Elapsed.TotalSeconds:F1}s ({errors} errors); waiting for the system to drain");

    var drained = await WaitDrain(t0, TimeSpan.FromSeconds(Math.Max(60, outageFor * 3)));
    if (!drained) Console.WriteLine("WARNING: not everything drained; numbers below cover what arrived");
    await Report(t0, legacyIds);
    return errors == 0 && drained ? 0 : 1;
}

async Task<int> Simulate(double perMinute, int maxOpen, int seed, CancellationToken ct)
{
    const int actor = 27;   // an AdventureWorks employee; the same one `edit` and `bench` use
    using var http = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromSeconds(15) };
    int[] planners;
    HashSet<int> apiProducts;
    try
    {
        planners = (await http.GetFromJsonAsync<JsonElement>("/api/planners")).EnumerateArray().Select(p => p.GetProperty("businessEntityId").GetInt32()).ToArray();
        apiProducts = (await http.GetFromJsonAsync<JsonElement>("/api/products")).EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToHashSet();
    }
    catch (Exception e) { Console.Error.WriteLine($"cannot reach the API at {api}: {e.Message}; are `make api` and `make relay` running?"); return 1; }
    if (planners.Length == 0) planners = [actor];

    // Only products that have an earlier routed order: release copies its operations from the latest one.
    var products = await Ints("SELECT ProductID FROM Production.Product p WHERE MakeFlag = 1 AND EXISTS (SELECT 1 FROM Production.WorkOrderRouting r WHERE r.ProductID = p.ProductID)");
    var reasons = await Ints("SELECT CAST(ScrapReasonID AS int) FROM Production.ScrapReason");
    var apiOk = products.Where(apiProducts.Contains).ToList();
    if (products.Count == 0 || reasons.Count == 0) { Console.Error.WriteLine("no routed products or scrap reasons in the legacy database"); return 1; }

    // Operation times come from the database server's clock (a container in UTC), not this host's.
    var clockOffset = (DateTime)(await Scalar("SELECT GETDATE()"))! - DateTime.Now;
    DateTime ServerNow() => DateTime.Now + clockOffset;

    var rnd = seed == 0 ? new Random() : new Random(seed);
    var pool = new List<SimOrder>();
    var pending = new List<PendingOrder>();
    var counts = new ConcurrentDictionary<string, int>();
    void Count(string k) => counts.AddOrUpdate(k, 1, (_, v) => v + 1);
    int N(string k) => counts.GetValueOrDefault(k);

    async Task<JsonElement?> PlannerCall(HttpMethod method, string path, object? body, string name)
    {
        using var span = LabTelemetry.Source.StartActivity("planner " + name, ActivityKind.Client);
        span?.SetTag("lab.synthetic", true);
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = JsonContent.Create(body);
        req.Headers.Add("X-Planner", planners[rnd.Next(planners.Length)].ToString());
        if (span?.Id != null) req.Headers.Add("traceparent", span.Id);
        using var r = await http.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"{(int)r.StatusCode} {text}");
        return text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.Clone();
    }

    void Fail(string what, Exception e)
    {
        Count(e is SqlException { Number: >= 50000 } or HttpRequestException ? "refused" : "errors");
        var msg = e.Message.ReplaceLineEndings(" ");
        Console.Error.WriteLine($"! {what}: {(msg.Length > 140 ? msg[..140] + "..." : msg)}");
    }

    async Task<bool> Try(string what, Task t)
    {
        try { await t; return true; }
        catch (Exception e) { Fail(what, e); return false; }
    }

    async Task ResolvePending()
    {
        if (pending.Count == 0) return;
        JsonElement? list = null;
        foreach (var p in pending.ToList())
        {
            try
            {
                if (p.NewId == null)
                {
                    list ??= await http.GetFromJsonAsync<JsonElement>("/api/work-orders?take=100");
                    foreach (var w in list.Value.EnumerateArray())
                        if (w.GetProperty("legacyWorkOrderId") is { ValueKind: JsonValueKind.Number } l && l.GetInt32() == p.LegacyId) p.NewId = w.GetProperty("id").GetGuid();
                }
                else if (p.LegacyId == null)
                {
                    var w = await http.GetFromJsonAsync<JsonElement>($"/api/work-orders/{p.NewId}");
                    if (w.GetProperty("legacyWorkOrderId") is { ValueKind: JsonValueKind.Number } l) p.LegacyId = l.GetInt32();
                }
            }
            catch { /* try again next tick */ }
            if (p.LegacyId != null && p.NewId != null)
            {
                pool.Add(new SimOrder { LegacyId = p.LegacyId.Value, NewId = p.NewId.Value, Qty = p.Qty, FloorQty = p.Qty, DueDate = p.Due });
                pending.Remove(p);
            }
            else if (DateTime.UtcNow - p.Since > TimeSpan.FromSeconds(60))
            {
                Console.Error.WriteLine($"! an order never reached the other side within 60 s (legacy {p.LegacyId}, new {p.NewId}); are `make relay` and `make api` running?");
                Count("errors"); pending.Remove(p);
            }
        }
    }

    async Task Create()
    {
        var qty = rnd.Next(4, 101) * 5;
        var due = DateTime.Today.AddDays(rnd.Next(7, 31));
        if (apiOk.Count > 0 && rnd.NextDouble() < 0.4)
        {
            var r = await PlannerCall(HttpMethod.Post, "/api/work-orders", new { productId = apiOk[rnd.Next(apiOk.Count)], orderQty = qty, dueDate = due }, "create");
            pending.Add(new PendingOrder { NewId = r!.Value.GetProperty("id").GetGuid(), Qty = qty, Due = due });
            Count("create/planner");
        }
        else
        {
            var id = (int)(await DesktopProc("create", "DECLARE @id int; EXEC dbo.uspWorkOrderCreate @p, @q, @d, @a, @id OUTPUT; SELECT @id;",
                ("@p", products[rnd.Next(products.Count)]), ("@q", qty), ("@d", due), ("@a", actor)))!;
            pending.Add(new PendingOrder { LegacyId = id, Qty = qty, Due = due });
            Count("create/desktop");
        }
    }

    Task PlannerEdit(SimOrder o, out Action done, string label)
    {
        if (Script.ScrapAmount(o, rnd) is var n && o.Scrapped < Script.ScrapCap(o) && rnd.NextDouble() < 0.5)
        {
            var reason = reasons[rnd.Next(reasons.Count)];
            done = () => { Script.DidScrap(o, n); Count("scrap/planner"); };
            return PlannerCall(HttpMethod.Post, $"/api/work-orders/{o.NewId}/scrap", new { quantity = n, reasonId = reason }, label + " scrap");
        }
        var qty = rnd.NextDouble() < 0.6 ? Script.NewQty(o, rnd) : (int?)null;
        var due = qty == null || rnd.NextDouble() < 0.5 ? Script.NewDue(o, rnd) : (DateTime?)null;
        done = () => { Script.DidPlan(o, qty, due); Count("plan"); };
        return PlannerCall(HttpMethod.Put, $"/api/work-orders/{o.NewId}/plan", new { orderQty = qty, dueDate = due }, label + " plan");
    }

    async Task Do(SimOrder o, Step step)
    {
        switch (step)
        {
            case Step.Release:
                await DesktopProc("release", "EXEC dbo.uspWorkOrderRelease @id, @a", ("@id", o.LegacyId), ("@a", actor));
                Script.DidRelease(o, (await Ints($"SELECT CAST(OperationSequence AS int) FROM Production.WorkOrderRouting WHERE WorkOrderID = {o.LegacyId} ORDER BY 1")).ToArray());
                Count("release"); break;
            case Step.StartOp:
            {
                var (i, start, hours) = Script.PlanStart(o, ServerNow(), rnd);
                await DesktopProc("routing start", "EXEC dbo.uspWorkOrderRoutingRecord @id, @seq, @s, NULL, NULL, NULL, @a", ("@id", o.LegacyId), ("@seq", (short)o.Ops[i]), ("@s", start), ("@a", actor));
                Script.DidStart(o, i, start, hours); Count("routing"); break;
            }
            case Step.FinishOp:
            {
                var (i, start, end, hours, cost) = Script.PlanFinish(o, ServerNow(), rnd);
                await DesktopProc("routing finish", "EXEC dbo.uspWorkOrderRoutingRecord @id, @seq, @s, @e, @h, @c, @a",
                    ("@id", o.LegacyId), ("@seq", (short)o.Ops[i]), ("@s", start), ("@e", end), ("@h", (decimal)hours), ("@c", cost), ("@a", actor));
                Script.DidFinish(o); Count("routing"); break;
            }
            case Step.Scrap when rnd.NextDouble() < 0.3:
            case Step.PlanChange:
            {
                var task = PlannerEdit(o, out var done, "planner");
                await task; done(); break;
            }
            case Step.Scrap:
            {
                var n = Script.ScrapAmount(o, rnd);
                await DesktopProc("scrap", "EXEC dbo.uspWorkOrderScrap @id, @n, @r, @a", ("@id", o.LegacyId), ("@n", (short)n), ("@r", (short)reasons[rnd.Next(reasons.Count)]), ("@a", actor));
                Script.DidScrap(o, n); Count("scrap/desktop"); break;
            }
            case Step.Close:
                await DesktopProc("close", "EXEC dbo.uspWorkOrderClose @id, @a", ("@id", o.LegacyId), ("@a", actor));
                Script.DidClose(o); Count("close"); break;
            case Step.Race:
            {
                // The one conflict a desktop user can really cause: closing an order while a planner edit is in flight.
                // Either the edit lands first, or the legacy side refuses it, logs the conflict and sends the state back.
                var close = Try("race close", DesktopProc("close", "EXEC dbo.uspWorkOrderClose @id, @a", ("@id", o.LegacyId), ("@a", actor)));
                var edit = Try("race edit", PlannerEdit(o, out var done, "race"));
                var ok = await Task.WhenAll(close, edit);
                if (ok[0]) { Script.DidClose(o); Count("close"); }
                if (ok[1]) done();
                Count("race"); break;
            }
        }
    }

    var clock = Stopwatch.StartNew();
    var lastReport = TimeSpan.Zero;
    var closed = 0;
    Console.WriteLine($"simulating about {perMinute}/min, up to {maxOpen} open orders; Ctrl+C to stop. Synthetic traffic: service workorder-desk-simulator, tag lab.synthetic=true.");
    while (!ct.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(-Math.Log(1 - rnd.NextDouble()) * 60 / perMinute), ct); }
        catch (OperationCanceledException) { break; }
        var burst = rnd.NextDouble() < 0.1 ? rnd.Next(3, 7) : 1;   // now and then several things happen at once
        for (var k = 0; k < burst && !ct.IsCancellationRequested; k++)
        {
            await ResolvePending();
            var canCreate = pool.Count + pending.Count < maxOpen;
            var what = "";
            try
            {
                if (pool.Count == 0 ? canCreate && pending.Count < 3 : canCreate && rnd.NextDouble() < 0.2) { what = "create"; await Create(); }
                else if (pool.Count > 0)
                {
                    var o = pool[rnd.Next(pool.Count)];
                    var step = Script.Next(o, rnd);
                    what = $"{step} on {o.LegacyId}";
                    await Do(o, step);
                    if (o.Closed) { pool.Remove(o); closed++; }
                }
            }
            catch (Exception e) { Fail(what, e); }
            if (k + 1 < burst) await Task.Delay(250);
        }
        if (clock.Elapsed - lastReport >= TimeSpan.FromSeconds(30))
        {
            lastReport = clock.Elapsed;
            Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] open {pool.Count} (+{pending.Count} syncing), closed {closed} | create {N("create/desktop")}+{N("create/planner")} release {N("release")} routing {N("routing")} " +
                              $"scrap {N("scrap/desktop")}+{N("scrap/planner")} plan {N("plan")} close {N("close")} races {N("race")} | refused {N("refused")}, errors {N("errors")}");
        }
    }
    Console.WriteLine($"stopped after {clock.Elapsed:hh\\:mm\\:ss}: {closed} orders closed, {pool.Count} left open, {N("race")} close/edit races, {N("refused")} refused, {N("errors")} errors");
    return N("errors") == 0 ? 0 : 1;
}

async Task<object?> Scalar(string sql)
{
    await using var c = new SqlConnection(legacy);
    await c.OpenAsync();
    return await new SqlCommand(sql, c).ExecuteScalarAsync();
}

async Task<List<int>> Ints(string sql)
{
    await using var c = new SqlConnection(legacy);
    await c.OpenAsync();
    await using var r = await new SqlCommand(sql, c).ExecuteReaderAsync();
    var list = new List<int>();
    while (await r.ReadAsync()) list.Add(r.GetInt32(0));
    return list;
}

void Docker(string args) => Process.Start(new ProcessStartInfo("docker", "compose " + args) { RedirectStandardOutput = true, RedirectStandardError = true })!.WaitForExit();

async Task<bool> WaitDrain(DateTime since, TimeSpan limit)
{
    var end = DateTime.UtcNow + limit;
    while (DateTime.UtcNow < end)
    {
        await using var s = new SqlConnection(legacy); await s.OpenAsync();
        await using var p = new NpgsqlConnection(postgres); await p.OpenAsync();
        var a = (int)(await new SqlCommand("SELECT COUNT(*) FROM sync.Outbox WHERE PublishedAt IS NULL", s).ExecuteScalarAsync())!;
        var b = (long)(await new NpgsqlCommand("SELECT COUNT(*) FROM outbox WHERE confirmed_at IS NULL AND created_at >= @s", p) { Parameters = { new("s", since.AddSeconds(-5)) } }.ExecuteScalarAsync())!;
        if (a == 0 && b == 0) { await Task.Delay(1500); return true; }
        await Task.Delay(1000);
    }
    return false;
}

static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
static string Row(string name, List<double> v)
{
    v.Sort();
    return $"| {name} | {v.Count} | {Pct(v, .5) * 1000:F0} | {Pct(v, .95) * 1000:F0} | {Pct(v, .99) * 1000:F0} | {(v.Count == 0 ? double.NaN : v[^1]) * 1000:F0} |";
}

/// <summary>Lag from the databases themselves: outbox row written (server clock) to the consumer's committed record
/// (server clock, same host). The consumer stamps the record a few milliseconds before its commit completes.</summary>
async Task Report(DateTime t0, List<int> legacyIds)
{
    await using var s = new SqlConnection(legacy); await s.OpenAsync();
    await using var p = new NpgsqlConnection(postgres); await p.OpenAsync();
    var from = t0.AddSeconds(-1);

    var created = new Dictionary<long, DateTime>();
    await using (var r = await new SqlCommand($"SELECT OutboxId, CreatedAt FROM sync.Outbox WHERE Origin = 'legacy' AND CreatedAt >= '{from:yyyy-MM-dd HH:mm:ss.fff}'", s).ExecuteReaderAsync())
        while (await r.ReadAsync()) created[r.GetInt64(0)] = DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc);
    var l2n = new List<(DateTime At, double Lag)>();
    await using (var r = await new NpgsqlCommand("SELECT message_id, processed_at FROM processed_message WHERE message_id LIKE 'legacy-%'", p).ExecuteReaderAsync())
        while (await r.ReadAsync())
            if (long.TryParse(r.GetString(0)["legacy-".Length..], out var id) && created.TryGetValue(id, out var c))
                l2n.Add((c, (r.GetDateTime(1).ToUniversalTime() - c).TotalSeconds));

    var inbox = new Dictionary<string, DateTime>();
    await using (var r = await new SqlCommand("SELECT MessageId, ProcessedAt FROM sync.Inbox WHERE MessageId LIKE 'new-%'", s).ExecuteReaderAsync())
        while (await r.ReadAsync()) inbox[r.GetString(0)] = DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc);
    var n2l = new List<(DateTime At, double Lag)>();
    var round = new List<double>();
    await using (var r = await new NpgsqlCommand("SELECT id, created_at, confirmed_at FROM outbox WHERE created_at >= @f AND type = 'RecordScrap'", p) { Parameters = { new("f", from) } }.ExecuteReaderAsync())
        while (await r.ReadAsync())
        {
            var made = r.GetDateTime(1).ToUniversalTime();
            if (inbox.TryGetValue($"new-{r.GetInt64(0)}", out var done)) n2l.Add((made, (done - made).TotalSeconds));
            if (!r.IsDBNull(2)) round.Add((r.GetDateTime(2).ToUniversalTime() - made).TotalSeconds);
        }

    Console.WriteLine();
    Console.WriteLine("| Path | Messages | p50 ms | p95 ms | p99 ms | max ms |");
    Console.WriteLine("|---|---|---|---|---|---|");
    Console.WriteLine(Row("legacy outbox → PostgreSQL commit", l2n.Select(x => x.Lag).ToList()));
    Console.WriteLine(Row("new outbox → SQL Server inbox", n2l.Select(x => x.Lag).ToList()));
    Console.WriteLine(Row("planner edit → confirmation back", round));

    Console.WriteLine();
    Console.WriteLine("Lag by 5 s of load (legacy → new, p95 and max in ms, by when the change was written):");
    Console.WriteLine("| from t+ | changes | p95 ms | max ms |");
    Console.WriteLine("|---|---|---|---|");
    foreach (var g in l2n.GroupBy(x => (int)((x.At - t0).TotalSeconds / 5)).OrderBy(g => g.Key))
    {
        var v = g.Select(x => x.Lag).ToList(); v.Sort();
        Console.WriteLine($"| {g.Key * 5} s | {v.Count} | {Pct(v, .95) * 1000:F0} | {v[^1] * 1000:F0} |");
    }
}

sealed class PendingOrder
{
    public int? LegacyId;
    public Guid? NewId;
    public int Qty;
    public DateTime Due;
    public DateTime Since = DateTime.UtcNow;
}
