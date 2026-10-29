using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Data;

namespace WorkOrders.Api.Erasure;

public record Needle(string Kind, string Value);

public record ErasureResult(Guid RequestId, int OutboxRowsRewritten, int QueueMessagesRedacted, string Detail);

/// <summary>Runs an erasure request end to end. The legacy side owns the person data, so the request travels to it as
/// a command through the same outbox, broker and consumer as every other change, it erases there, and the answer
/// travels back as an event that makes this side purge its copies. The service then redacts what is still in the
/// queues. It needs the relays and consumers running (`make api`, `make relay`).</summary>
public class ErasureService(LabDbContext db, IDeadLetterAdmin broker, TimeSpan? timeout = null)
{
    private static readonly string[] AllQueues =
    [
        Topology.NewSideQueue, Topology.LegacyQueue, Topology.NewSideDeadLetterQueue, Topology.LegacyDeadLetterQueue,
        "newside.workorders.retry", "legacy.commands.retry",
    ];

    private TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(60);

    public async Task<ErasureResult> EraseAsync(int subject, CancellationToken ct = default)
    {
        var done = await db.ErasureLog.AsNoTracking().FirstOrDefaultAsync(l => l.SubjectBusinessEntityId == subject && l.NewPurgedAt != null, ct);
        if (done != null) return new(done.RequestId, 0, 0, "already erased: " + done.Detail);

        var requestId = Guid.CreateVersion7();
        db.ErasureLog.Add(new ErasureLogEntry { RequestId = requestId, SubjectBusinessEntityId = subject, RequestedAt = DateTime.UtcNow });
        // 1. Stop new data being created about them: the planner can no longer sign in, and the label goes.
        var planner = await db.Planners.SingleOrDefaultAsync(p => p.BusinessEntityId == subject, ct);
        if (planner != null) { planner.ErasedAt ??= DateTime.UtcNow; planner.DisplayName = null; }
        await db.SaveChangesAsync(ct);

        // 2. Let their commands that are already on the way finish, so none can arrive after the erasure. A command
        //    stuck in a dead-letter queue is not waited for: it is redacted below.
        await WaitAsync(async () => (await InFlightActorCommandsAsync(subject, ct)).Count == 0, "the planner's commands in flight", ct);

        // 3. Ask the owner of the data to erase it, through the broker.
        db.Outbox.Add(new OutboxMessage
        {
            Type = Topology.EraseSubject, SubjectBusinessEntityId = subject, NewId = requestId, CreatedAt = DateTime.UtcNow,
            TraceParent = System.Diagnostics.Activity.Current?.Id,
            Payload = JsonSerializer.Serialize(new { requestId }),
        });
        await db.SaveChangesAsync(ct);

        // 4. Wait for its answer; the handler purges this side's copies when it arrives.
        await WaitAsync(async () =>
        {
            db.ChangeTracker.Clear();
            return (await db.ErasureLog.AsNoTracking().SingleAsync(l => l.RequestId == requestId, ct)).NewPurgedAt != null;
        }, "the legacy side's erasure confirmation", ct);

        // 5. Messages still queued (dead-lettered or waiting to retry) may carry the actor reference.
        var redacted = await broker.RedactActorAsync(AllQueues, subject, ct);
        var log = await db.ErasureLog.AsNoTracking().SingleAsync(l => l.RequestId == requestId, ct);
        return new(requestId, 0, redacted, $"{log.Detail}; {redacted} queued message(s) redacted");
    }

    /// <summary>Unconfirmed commands by this actor that are not stuck in a dead-letter queue.</summary>
    private async Task<List<long>> InFlightActorCommandsAsync(int subject, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var rows = await db.Outbox.AsNoTracking().Where(o => o.ConfirmedAt == null && o.Type != Topology.EraseSubject).ToListAsync(ct);
        var mine = rows.Where(o =>
        {
            try { using var d = JsonDocument.Parse(o.Payload); return d.RootElement.TryGetProperty("actorBusinessEntityId", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == subject; }
            catch (JsonException) { return false; }
        }).Select(o => o.Id).ToList();
        if (mine.Count == 0) return mine;
        var dead = (await broker.PeekAsync(Topology.LegacyDeadLetterQueue, ct)).Select(d => d.CommandId).ToHashSet();
        return mine.Where(id => !dead.Contains($"new-{id}")).ToList();
    }

    private async Task WaitAsync(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        var end = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("erasure timed out waiting for " + what + "; it is safe to run it again");
    }

    /// <summary>What identifies the subject, read from the owning side before it is erased: the strings the check searches for.
    /// A value that also occurs inside another person's data is left out, because finding it afterwards would prove
    /// nothing about the subject.</summary>
    public static async Task<List<Needle>> CollectNeedlesAsync(string legacyConnectionString, int subject, CancellationToken ct = default)
    {
        var needles = new List<Needle>();
        await using var c = new SqlConnection(legacyConnectionString);
        await c.OpenAsync(ct);

        async Task<List<string>> Strings(string sql)
        {
            await using var cmd = new SqlCommand(sql, c);
            cmd.Parameters.AddWithValue("@id", subject);
            var l = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) if (!r.IsDBNull(0)) l.Add(r.GetString(0));
            return l;
        }

        // The full name is searched as one string, which no single column of the sample holds for anyone else.
        foreach (var n in await Strings("SELECT FirstName + N' ' + LastName FROM Person.Person WHERE BusinessEntityID = @id")) needles.Add(new("full name", n));
        // Everything else is left out if it also occurs inside another row's value (a short email inside a longer one):
        // finding it afterwards would prove nothing about the subject.
        foreach (var n in await Strings(@"SELECT x.EmailAddress FROM Person.EmailAddress x WHERE x.BusinessEntityID = @id
            AND NOT EXISTS (SELECT 1 FROM Person.EmailAddress o WHERE o.BusinessEntityID <> @id AND o.EmailAddress LIKE N'%' + x.EmailAddress + N'%')")) needles.Add(new("email address", n));
        foreach (var n in await Strings(@"SELECT x.NationalIDNumber FROM HumanResources.Employee x WHERE x.BusinessEntityID = @id
            AND NOT EXISTS (SELECT 1 FROM HumanResources.Employee o WHERE o.BusinessEntityID <> @id AND o.NationalIDNumber LIKE N'%' + x.NationalIDNumber + N'%')")) needles.Add(new("national id", n));
        foreach (var n in await Strings(@"SELECT x.LoginID FROM HumanResources.Employee x WHERE x.BusinessEntityID = @id
            AND NOT EXISTS (SELECT 1 FROM HumanResources.Employee o WHERE o.BusinessEntityID <> @id AND o.LoginID LIKE N'%' + x.LoginID + N'%')")) needles.Add(new("login id", n));
        foreach (var n in await Strings(@"SELECT x.PhoneNumber FROM Person.PersonPhone x WHERE x.BusinessEntityID = @id
            AND NOT EXISTS (SELECT 1 FROM Person.PersonPhone o WHERE o.BusinessEntityID <> @id AND o.PhoneNumber LIKE N'%' + x.PhoneNumber + N'%')")) needles.Add(new("phone number", n));
        foreach (var n in await Strings(@"SELECT a.AddressLine1 FROM Person.BusinessEntityAddress b JOIN Person.Address a ON a.AddressID = b.AddressID WHERE b.BusinessEntityID = @id
            AND NOT EXISTS (SELECT 1 FROM Person.Address o WHERE o.AddressID <> a.AddressID AND (o.AddressLine1 LIKE N'%' + a.AddressLine1 + N'%' OR o.AddressLine2 LIKE N'%' + a.AddressLine1 + N'%'))"))
            needles.Add(new("street address", n));
        return needles;
    }
}
