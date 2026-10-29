using Lab.Observability;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;
using WorkOrders.Api.Data;
using WorkOrders.Api.Import;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<LabDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not set")).UseSnakeCaseNamingConvention());
builder.Services.AddAuthentication(WorkOrders.Api.Auth.DevAuthHandler.Scheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, WorkOrders.Api.Auth.DevAuthHandler>(WorkOrders.Api.Auth.DevAuthHandler.Scheme, null);
builder.Services.AddAuthorization();
builder.Services.AddLabTelemetry("workorder-desk-api").WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource("Npgsql"));
builder.Services.AddHostedService<WorkOrders.Api.Sync.SyncMetrics>();
builder.Services.AddScoped<WorkOrders.Api.Commands.WorkOrderCommands>();
builder.Services.AddSingleton<WorkOrders.Api.Sync.BrokerConnection>();
builder.Services.AddHostedService<WorkOrders.Api.Sync.OutboxRelay>();
builder.Services.AddSingleton<WorkOrders.Api.Reconcile.ReconciliationState>();
builder.Services.AddSingleton<WorkOrders.Api.Reconcile.ReconcileTimer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkOrders.Api.Reconcile.ReconcileTimer>());
builder.Services.AddScoped<WorkOrders.Api.Sync.WorkOrderSyncHandler>();
builder.Services.AddHostedService<WorkOrders.Api.Sync.SyncConsumer>();
var app = builder.Build();

// `dotnet run -- migrate` applies EF migrations; `-- import` runs the one-off legacy import.
if (args.Length > 0)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LabDbContext>();
    switch (args[0])
    {
        case "migrate":
            await db.Database.MigrateAsync();
            Console.WriteLine("migrated");
            return;
        case "import":
            var legacy = app.Configuration.GetConnectionString("LegacySql")
                ?? throw new InvalidOperationException("ConnectionStrings:LegacySql is not set");
            var window = args.Length > 1 ? int.Parse(args[1]) : 1000;
            var n = await new LegacyImporter(db, legacy, app.Configuration.GetSection("Planners:Ids").Get<int[]>()).RunAsync(window);
            Console.WriteLine($"imported {n} work orders");
            return;
        case "erase":
            // `erase <BusinessEntityID>`: collect what identifies the person, erase through the running relays, then scan
            // everything. Prints identifier KINDS and counts, never values. Exit 1 if anything is left.
            {
                var subject = int.Parse(args[1]);
                var conn = app.Configuration.GetConnectionString("LegacySql") ?? throw new InvalidOperationException("ConnectionStrings:LegacySql is not set");
                var pg = app.Configuration.GetConnectionString("Postgres")!;
                var uri = app.Configuration["RabbitMq:Uri"] ?? throw new InvalidOperationException("RabbitMq:Uri is not set");
                await using var bus = await Sync.RabbitMq.RabbitMqBus.ConnectAsync(uri);
                var needles = await WorkOrders.Api.Erasure.ErasureService.CollectNeedlesAsync(conn, subject);
                var verifier = new WorkOrders.Api.Erasure.ErasureVerifier(pg, conn, bus);
                var before = await verifier.ScanAsync(needles, subject);
                Console.WriteLine($"before: {before.Count} place(s) hold the subject ({string.Join(", ", before.Select(h => h.Store + ":" + h.Where + "/" + h.Kind).Distinct())})");
                var result = await new WorkOrders.Api.Erasure.ErasureService(db, bus).EraseAsync(subject);
                var after = await verifier.ScanAsync(needles, subject);
                Console.WriteLine($"erased, request {result.RequestId}: {result.Detail}");
                Console.WriteLine($"after: {after.Count} place(s) hold the subject");
                foreach (var h in after) Console.WriteLine($"  LEFT: {h.Store} {h.Where} ({h.Kind}) x{h.Count}");
                Environment.ExitCode = after.Count == 0 ? 0 : 1;
            }
            return;
        case "reconcile":
        case "redrive":
            var rmq = app.Configuration["RabbitMq:Uri"];
            await using (var bus = string.IsNullOrEmpty(rmq) ? null : await Sync.RabbitMq.RabbitMqBus.ConnectAsync(rmq))
            {
                if (args[0] == "redrive")
                {
                    if (bus == null) throw new InvalidOperationException("RabbitMq:Uri is not set");
                    foreach (var q in new[] { Sync.Contracts.Topology.NewSideDeadLetterQueue, Sync.Contracts.Topology.LegacyDeadLetterQueue })
                        Console.WriteLine($"{q}: {await bus.RedriveAsync(q)} message(s) put back");
                    return;
                }
                var conn = app.Configuration.GetConnectionString("LegacySql") ?? throw new InvalidOperationException("ConnectionStrings:LegacySql is not set");
                var report = await new WorkOrders.Api.Reconcile.Reconciler(db, conn, bus).RunAsync();
                Console.WriteLine(report.ToMarkdown());
                Console.WriteLine("written to " + WorkOrders.Api.Reconcile.ReportFiles.Write(report));
                Environment.ExitCode = report.Unexplained > 0 ? 1 : 0;
            }
            return;
        default:
            Console.Error.WriteLine("unknown command " + args[0]);
            Environment.ExitCode = 2;
            return;
    }
}

app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/work-orders", async (LabDbContext db, string? status, int? take, int? skip) =>
{
    IQueryable<WorkOrder> q = db.WorkOrders.AsNoTracking().Include(w => w.Operations);
    // Same derivation as WorkOrder.Status, written so it runs in the database before paging.
    q = status switch
    {
        "Closed" => q.Where(w => w.EndDate != null),
        "In progress" => q.Where(w => w.EndDate == null && w.Operations.Any(o => o.ActualStartDate != null)),
        "Released" => q.Where(w => w.EndDate == null && w.Operations.Any() && !w.Operations.Any(o => o.ActualStartDate != null)),
        "Created" => q.Where(w => w.EndDate == null && !w.Operations.Any()),
        _ => q,
    };
    var rows = await q.OrderByDescending(w => w.LegacyWorkOrderId).Skip(skip ?? 0).Take(Math.Min(take ?? 100, 500)).ToListAsync();
    return rows.Select(WorkOrderDto.From);
});

app.MapGet("/api/work-orders/{id:guid}", async (LabDbContext db, Guid id) =>
{
    var w = await db.WorkOrders.AsNoTracking().Include(x => x.Operations).SingleOrDefaultAsync(x => x.Id == id);
    return w is null ? Results.NotFound() : Results.Ok(WorkOrderDto.From(w));
});

app.MapGet("/api/planners", async (LabDbContext db) => await db.Planners.AsNoTracking().Where(p => p.ErasedAt == null)
    .OrderBy(p => p.BusinessEntityId).Select(p => new { businessEntityId = p.BusinessEntityId, name = p.DisplayName }).ToListAsync());
app.MapGet("/api/products", async (LabDbContext db) => await db.WorkOrders.AsNoTracking()
    .GroupBy(w => new { w.LegacyProductId, w.ProductName }).Select(g => new { id = g.Key.LegacyProductId, name = g.Key.ProductName })
    .OrderBy(p => p.name).ToListAsync());
app.MapGet("/api/scrap-reasons", async (LabDbContext db) => await db.ScrapReasons.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

int Actor(System.Security.Claims.ClaimsPrincipal u) => int.Parse(u.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
IResult Reply(WorkOrders.Api.Commands.CommandResponse r) => r.Outcome switch
{
    WorkOrders.Api.Commands.CommandOutcome.Accepted => Results.Accepted($"/api/work-orders/{r.Id}", new { id = r.Id }),
    WorkOrders.Api.Commands.CommandOutcome.NotFound => Results.NotFound(),
    WorkOrders.Api.Commands.CommandOutcome.Invalid => Results.BadRequest(new { error = r.Error }),
    _ => Results.Conflict(new { error = r.Error }),
};

app.MapPost("/api/work-orders", async (System.Security.Claims.ClaimsPrincipal u, WorkOrders.Api.Commands.WorkOrderCommands c, CreateRequest r) =>
    Reply(await c.CreateAsync(Actor(u), r.ProductId, r.OrderQty, r.DueDate))).RequireAuthorization();
app.MapPut("/api/work-orders/{id:guid}/plan", async (System.Security.Claims.ClaimsPrincipal u, WorkOrders.Api.Commands.WorkOrderCommands c, Guid id, PlanRequest r) =>
    Reply(await c.ChangePlanAsync(Actor(u), id, r.OrderQty, r.DueDate))).RequireAuthorization();
app.MapPost("/api/work-orders/{id:guid}/scrap", async (System.Security.Claims.ClaimsPrincipal u, WorkOrders.Api.Commands.WorkOrderCommands c, Guid id, ScrapRequest r) =>
    Reply(await c.ScrapAsync(Actor(u), id, r.Quantity, r.ReasonId))).RequireAuthorization();

app.MapGet("/api/reconciliation", (WorkOrders.Api.Reconcile.ReconciliationState st) =>
    st.Latest is { } r ? Results.Ok(new { r.At, r.Compared, r.Equal, r.Unexplained, r.Differences, r.Conflicts, r.Warnings, r.DeadLetters }) : Results.NotFound());
app.MapPost("/api/reconciliation/run", async (WorkOrders.Api.Reconcile.ReconcileTimer t, IConfiguration c) =>
{
    var r = await t.RunOnceAsync(c.GetConnectionString("LegacySql")!, default);
    return Results.Ok(new { r.At, r.Compared, r.Equal, r.Unexplained, r.Differences, r.Conflicts, r.Warnings, r.DeadLetters });
}).RequireAuthorization();

app.MapFallbackToFile("index.html");
app.Run();

record CreateRequest(int ProductId, int OrderQty, DateTime DueDate);
record PlanRequest(int? OrderQty, DateTime? DueDate);
record ScrapRequest(int Quantity, int ReasonId);

record OperationDto(short OperationSequence, string WorkCenter, DateTime ScheduledStartDate, DateTime ScheduledEndDate,
    DateTime? ActualStartDate, DateTime? ActualEndDate, decimal? ActualResourceHrs, decimal PlannedCost, decimal? ActualCost);

record WorkOrderDto(Guid Id, int? LegacyWorkOrderId, int ProductId, string ProductName, int OrderQty, int ScrappedQty,
    int StockedQty, DateTime StartDate, DateTime DueDate, DateTime? EndDate, string Status, IEnumerable<OperationDto> Operations)
{
    public static WorkOrderDto From(WorkOrder w) => new(w.Id, w.LegacyWorkOrderId, w.LegacyProductId, w.ProductName,
        w.OrderQty, w.ScrappedQty, w.OrderQty - w.ScrappedQty, w.StartDate, w.DueDate, w.EndDate, w.Status,
        w.Operations.OrderBy(o => o.OperationSequence).Select(o => new OperationDto(o.OperationSequence, o.LocationName,
            o.ScheduledStartDate, o.ScheduledEndDate, o.ActualStartDate, o.ActualEndDate, o.ActualResourceHrs, o.PlannedCost, o.ActualCost)));
}
