using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Sync.Contracts;

namespace Sync.Legacy;

public enum CommandResult
{
    Applied,
    Duplicate,
    /// <summary>The legacy side refused on a business rule (the order is closed or gone). Nothing was changed, the
    /// conflict is logged, and the order's current state is sent back so the new side drops its optimistic edit.</summary>
    Rejected,
}

/// <summary>Applies a command from the new side by calling the legacy stored procedures, so AdventureWorks'
/// own triggers (iWorkOrder/uWorkOrder -> TransactionHistory) and the lab's outbox triggers still fire.
/// The inbox row, the session markers and the procedure call share one transaction.</summary>
public class LegacyCommandHandler(string connectionString)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CommandResult> HandleAsync(Message m, CancellationToken ct = default)
    {
        using var span = LabTelemetry.Source.StartActivity("apply " + m.Type + " on legacy");
        span.TagOrder(m);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            try
            {
                await using var inbox = new SqlCommand("INSERT sync.Inbox (MessageId) VALUES (@id)", conn, tx);
                inbox.Parameters.AddWithValue("@id", m.MessageId);
                await inbox.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                await tx.RollbackAsync(ct);
                return CommandResult.Duplicate;   // already applied
            }

            // Origin marker: the outbox triggers record that this change came from the new side, so the relay
            // sends it back as a confirmation, never as a legacy change. A create also carries the new side's id.
            await SetContext(conn, tx, "sync.origin", "new", ct);
            if (m.NewId != null) await SetContext(conn, tx, "sync.new_id", m.NewId, ct);
            // The trigger copies this into sync.Outbox, so the confirmation continues the same trace.
            var traceparent = span?.Id ?? m.TraceParent;
            if (traceparent != null) await SetContext(conn, tx, "traceparent", traceparent, ct);
            await SetContext(conn, tx, "sync.command_id", m.MessageId, ct);

            if (m.Type is Topology.ChangePlan or Topology.RecordScrap)
            {
                var order = await ReadOrder(conn, tx, (int)m.AggregateId, ct);
                if (order is null || order.Value.EndDate != null)
                {
                    // Conflict policy: a closed order cannot be edited. The floor closed it, so the legacy side wins.
                    await LogConflict(conn, tx, (int)m.AggregateId, order is null ? "Missing" : "Closed", null,
                        order?.EndDate?.ToString("s"), m.Type, "legacy", m.MessageId, ct);
                    if (order is not null) await Exec(conn, tx, "sync.uspResync", ct, ("@WorkOrderID", (int)m.AggregateId));
                    await tx.CommitAsync(ct);
                    return CommandResult.Rejected;
                }
                if (m.Type == Topology.ChangePlan) await DetectPlanConflicts(conn, tx, m, order.Value, ct);
            }

            switch (m.Type)
            {
                case Topology.CreateWorkOrder:
                    var c = JsonSerializer.Deserialize<CreateWorkOrderCommand>(m.Body.Span, Json)!;
                    await Exec(conn, tx, "dbo.uspWorkOrderCreate", ct,
                        ("@ProductID", c.ProductId), ("@OrderQty", c.OrderQty), ("@DueDate", c.DueDate),
                        ("@ActorBusinessEntityID", c.ActorBusinessEntityId), ("@WorkOrderID", new OutInt()));
                    break;
                case Topology.ChangePlan:
                    var p = JsonSerializer.Deserialize<ChangePlanCommand>(m.Body.Span, Json)!;
                    await Exec(conn, tx, "dbo.uspWorkOrderChangePlan", ct,
                        ("@WorkOrderID", (int)m.AggregateId), ("@OrderQty", (object?)p.OrderQty ?? DBNull.Value),
                        ("@DueDate", (object?)p.DueDate ?? DBNull.Value), ("@ActorBusinessEntityID", p.ActorBusinessEntityId));
                    break;
                case Topology.RecordScrap:
                    var s = JsonSerializer.Deserialize<RecordScrapCommand>(m.Body.Span, Json)!;
                    await Exec(conn, tx, "dbo.uspWorkOrderScrap", ct,
                        ("@WorkOrderID", (int)m.AggregateId), ("@ScrapQty", (short)s.Quantity),
                        ("@ScrapReasonID", (short)s.ReasonId), ("@ActorBusinessEntityID", s.ActorBusinessEntityId));
                    break;
                case Topology.EraseSubject:
                    // m.NewId carries the erasure request id.
                    await Exec(conn, tx, "dbo.uspEraseSubject", ct,
                        ("@SubjectBusinessEntityID", (int)m.AggregateId), ("@RequestId", Guid.Parse(m.NewId!)));
                    break;
                default:
                    throw new InvalidOperationException("unknown command type " + m.Type);
            }
            await tx.CommitAsync(ct);
            return CommandResult.Applied;
        }
        finally
        {
            // Pooled connections: never leave the origin marker behind for the next user of this connection.
            try
            {
                if (conn.State == ConnectionState.Open)
                {
                    await using var clear = new SqlCommand(
                        @"EXEC sp_set_session_context N'sync.origin', NULL; EXEC sp_set_session_context N'sync.new_id', NULL;
                          EXEC sp_set_session_context N'traceparent', NULL; EXEC sp_set_session_context N'sync.command_id', NULL;", conn);
                    await clear.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            catch { /* connection already broken; the pool discards it */ }
        }
    }

    private sealed class OutInt;

    private readonly record struct Order(int OrderQty, DateTime DueDate, DateTime? EndDate);

    private static async Task<Order?> ReadOrder(SqlConnection conn, SqlTransaction tx, int id, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT OrderQty, DueDate, EndDate FROM Production.WorkOrder WITH (UPDLOCK) WHERE WorkOrderID = @id", conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Order(r.GetInt32(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetDateTime(2));
    }

    /// <summary>Compare-and-set against what the planner saw. Policy: quantity and due date belong to the planner
    /// desk, so the new side wins; the overwritten legacy value is logged so nothing is silently lost.</summary>
    private static async Task DetectPlanConflicts(SqlConnection conn, SqlTransaction tx, Message m, Order now, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<ChangePlanCommand>(m.Body.Span, Json)!;
        if (p.OrderQty != null && p.BaseOrderQty != null && p.BaseOrderQty != now.OrderQty)
            await LogConflict(conn, tx, (int)m.AggregateId, "OrderQty", p.BaseOrderQty.ToString(), now.OrderQty.ToString(), p.OrderQty.ToString(), "new", m.MessageId, ct);
        if (p.DueDate != null && p.BaseDueDate != null && p.BaseDueDate != now.DueDate)
            await LogConflict(conn, tx, (int)m.AggregateId, "DueDate", p.BaseDueDate?.ToString("s"), now.DueDate.ToString("s"), p.DueDate?.ToString("s"), "new", m.MessageId, ct);
    }

    private static async Task LogConflict(SqlConnection conn, SqlTransaction tx, int id, string field, string? baseValue,
        string? legacyValue, string? newValue, string winner, string messageId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            @"INSERT sync.ConflictLog (WorkOrderID, Field, BaseValue, LegacyValue, NewValue, Winner, MessageId)
              VALUES (@id, @f, @b, @l, @n, @w, @m)", conn, tx);
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@f", field);
        cmd.Parameters.AddWithValue("@b", (object?)baseValue ?? DBNull.Value); cmd.Parameters.AddWithValue("@l", (object?)legacyValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@n", (object?)newValue ?? DBNull.Value); cmd.Parameters.AddWithValue("@w", winner);
        cmd.Parameters.AddWithValue("@m", messageId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetContext(SqlConnection conn, SqlTransaction tx, string key, string value, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("EXEC sp_set_session_context @k, @v", conn, tx);
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Exec(SqlConnection conn, SqlTransaction tx, string proc, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new SqlCommand(proc, conn, tx) { CommandType = CommandType.StoredProcedure };
        foreach (var (name, value) in args)
            cmd.Parameters.Add(value is OutInt
                ? new SqlParameter(name, SqlDbType.Int) { Direction = ParameterDirection.Output }
                : new SqlParameter(name, value));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
