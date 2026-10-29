using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WorkOrders.Api.Data;

namespace WorkOrders.Api.Import;

/// <summary>One-off, idempotent import of the legacy work orders: every open order plus the most recent
/// closed ones (not all of history). Re-running updates existing rows by legacy id.</summary>
public class LegacyImporter(LabDbContext db, string legacyConnectionString, IReadOnlyCollection<int>? plannerIds = null)
{
    public async Task<int> RunAsync(int recentWindow, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(legacyConnectionString);
        await conn.OpenAsync(ct);

        var reasons = new Dictionary<int, string>();
        await using (var cmd = new SqlCommand("SELECT ScrapReasonID, Name FROM Production.ScrapReason", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) reasons[r.GetInt16(0)] = r.GetString(1);
        var known = await db.ScrapReasons.ToDictionaryAsync(x => x.Id, ct);
        foreach (var (id, name) in reasons)
            if (known.TryGetValue(id, out var existingReason)) existingReason.Name = name;
            else db.ScrapReasons.Add(new ScrapReason { Id = id, Name = name });

        // Planners: the id and a display label. The label is the only personal data the new side keeps about a person.
        foreach (var pid in plannerIds ?? [])
        {
            await using var cmd = new SqlCommand("SELECT FirstName + N' ' + LastName FROM Person.Person WHERE BusinessEntityID = @id", conn);
            cmd.Parameters.AddWithValue("@id", pid);
            var name = (string?)await cmd.ExecuteScalarAsync(ct);
            var existingPlanner = await db.Planners.FindAsync([pid], ct);
            if (existingPlanner == null) db.Planners.Add(new Planner { BusinessEntityId = pid, DisplayName = name });
            else if (existingPlanner.ErasedAt == null) existingPlanner.DisplayName = name;   // never bring an erased planner back
        }

        var orders = new Dictionary<int, WorkOrder>();
        await using (var cmd = new SqlCommand(@"
            SELECT w.WorkOrderID, w.ProductID, p.Name, w.OrderQty, w.ScrappedQty, w.StartDate, w.EndDate, w.DueDate, w.ScrapReasonID
            FROM Production.WorkOrder w JOIN Production.Product p ON p.ProductID = w.ProductID
            WHERE w.EndDate IS NULL OR w.WorkOrderID IN (SELECT TOP (@n) WorkOrderID FROM Production.WorkOrder ORDER BY WorkOrderID DESC)", conn))
        {
            cmd.Parameters.AddWithValue("@n", recentWindow);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var o = new WorkOrder
                {
                    LegacyWorkOrderId = r.GetInt32(0), LegacyProductId = r.GetInt32(1), ProductName = r.GetString(2),
                    OrderQty = r.GetInt32(3), ScrappedQty = r.GetInt16(4), StartDate = r.GetDateTime(5),
                    EndDate = r.IsDBNull(6) ? null : r.GetDateTime(6), DueDate = r.GetDateTime(7),
                    ScrapReasonId = r.IsDBNull(8) ? null : r.GetInt16(8),
                };
                orders[o.LegacyWorkOrderId!.Value] = o;
            }
        }

        var ops = new Dictionary<int, List<WorkOrderOperation>>();
        await using (var cmd = new SqlCommand(@"
            SELECT r.WorkOrderID, r.OperationSequence, r.LocationID, l.Name, r.ScheduledStartDate, r.ScheduledEndDate,
                   r.ActualStartDate, r.ActualEndDate, r.ActualResourceHrs, r.PlannedCost, r.ActualCost
            FROM Production.WorkOrderRouting r JOIN Production.Location l ON l.LocationID = r.LocationID
            WHERE r.WorkOrderID IN (SELECT WorkOrderID FROM Production.WorkOrder w
                                    WHERE w.EndDate IS NULL OR w.WorkOrderID IN (SELECT TOP (@n) WorkOrderID FROM Production.WorkOrder ORDER BY WorkOrderID DESC))", conn))
        {
            cmd.Parameters.AddWithValue("@n", recentWindow);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                if (!ops.TryGetValue(id, out var list)) ops[id] = list = new();
                list.Add(new WorkOrderOperation
                {
                    OperationSequence = r.GetInt16(1), LocationId = r.GetInt16(2), LocationName = r.GetString(3),
                    ScheduledStartDate = r.GetDateTime(4), ScheduledEndDate = r.GetDateTime(5),
                    ActualStartDate = r.IsDBNull(6) ? null : r.GetDateTime(6), ActualEndDate = r.IsDBNull(7) ? null : r.GetDateTime(7),
                    ActualResourceHrs = r.IsDBNull(8) ? null : r.GetDecimal(8), PlannedCost = r.GetDecimal(9),
                    ActualCost = r.IsDBNull(10) ? null : r.GetDecimal(10),
                });
            }
        }

        var ids = orders.Keys.ToList();
        var existing = await db.WorkOrders.Include(w => w.Operations)
            .Where(w => w.LegacyWorkOrderId != null && ids.Contains(w.LegacyWorkOrderId.Value))
            .ToDictionaryAsync(w => w.LegacyWorkOrderId!.Value, ct);

        foreach (var (legacyId, src) in orders)
        {
            var srcOps = ops.GetValueOrDefault(legacyId) ?? new();
            if (!existing.TryGetValue(legacyId, out var dst))
            {
                src.Operations = srcOps;
                db.WorkOrders.Add(src);
                continue;
            }
            dst.LegacyProductId = src.LegacyProductId; dst.ProductName = src.ProductName; dst.OrderQty = src.OrderQty;
            dst.ScrappedQty = src.ScrappedQty; dst.StartDate = src.StartDate; dst.EndDate = src.EndDate;
            dst.DueDate = src.DueDate; dst.ScrapReasonId = src.ScrapReasonId;
            db.Operations.RemoveRange(dst.Operations);
            dst.Operations = srcOps;
        }
        await db.SaveChangesAsync(ct);
        return orders.Count;
    }
}
