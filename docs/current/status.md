# Status (2026-10-12)

| Slice | State |
|---|---|
| L0 repo, Compose, AdventureWorks, licence notice | Done. `make up` restores the database and applies `legacy/sql/V*.sql` |
| L1 legacy procedures | Done and run end to end through `make smoke` and as the `lab_app` login |
| L1 WPF client | Builds on Linux (`make wpf-build`, **net48** since L7), deployed with `make wpf-deploy` to the `win2k12r2` VM (Windows Server 2012 R2, fixed lease, OpenSSH, .NET Framework 4.8) and **run there** (2026-10-17): create, release, routing, scrap and close reach PostgreSQL, and its own spans (`workorder-desk-wpf-client`) start the traces. Evidence in [verification.md](verification.md) |
| L2 read-only new side | Done: PostgreSQL schema (EF Core migration `Initial`), one-off import, ASP.NET Core API, React list and detail on port 32130. Parity with the legacy view checked |
| L3 legacy → new sync | Done for the normal path: outbox triggers, relay, RabbitMQ, idempotent consumer. Lag is measured under L7 |
| L4 new → legacy sync | Done for the normal path: planner create, plan change (quantity, due date) and scrap through the API reach SQL Server via the stored procedures, with confirmation back. No echo loop |
| L5 failure cases | Done: eight cases, each with a reproducing test, observed outcome and rule: [failure-cases.md](failure-cases.md). Retry queue and dead-letter queue added to the bus |
| L6 conflicts and reconciliation | Done: conflict policy per field group with logs on both sides, closed-order refusal, dead-letter re-drive, and a reconciliation report that classifies every difference: [conflicts-and-reconciliation.md](conflicts-and-reconciliation.md) |
| L7 OpenTelemetry | Done, including the WPF client's own spans (run 2026-10-17): traces across all hops, metrics, provisioned dashboard, `make bench`, measurements: [observability.md](observability.md) |
| L8 GDPR erasure | Done: erasure across SQL Server, PostgreSQL and the broker, a scan that proves it, minimal erasure logs: [erasure.md](erasure.md) |

## L1 design as built

- Lifecycle is derived by `dbo.vWorkOrderStatus` (created, released, in progress, closed). No
  column was added to Microsoft's tables.
- `dbo.WorkOrderAudit` records the acting employee per call (AdventureWorks has no person column
  on work orders).
- `uspWorkOrderRelease` copies the operations of the product's most recent earlier work order,
  shifting the dates; it fails with 50013 if the product has none.
- `uspWorkOrderClose` requires every operation to have an actual end date.
- The login `lab_app` has `db_datareader` and `EXECUTE` on schema `dbo`. A direct insert into
  `Production.WorkOrder` is denied.

## Known limitations

- The WPF screens do no input validation beyond the procedures' errors.
- A .NET 4.5 client talking to SQL Server 2022 over TLS from Server 2012 R2 is untested; the
  login packet is TLS-protected even with `Encrypt` off. If it fails, check the VM's TLS 1.2
  setting before changing the client.
- Test work orders created by `make smoke` stay until `make reset`.
- The relay and consumers run from the working tree (`make relay`, `make api`); they are not in Compose yet.
- Dead-letter queues are re-driven by hand (`make redrive`) and not alerted on.
- The React edit forms build and typecheck but have not been looked at in a browser.


## L2 design as built

- `src/WorkOrders.Api`: EF Core 10 with Npgsql and snake_case columns. `work_order` has a uuid v7
  key and a unique nullable `legacy_work_order_id`; `work_order_operation` is keyed by
  `(work_order_id, operation_sequence)`, and the legacy composite key is rebuilt from the parent's
  legacy id, `legacy_product_id` and the sequence.
- Legacy datetimes are stored as `timestamp without time zone`: the legacy side has no zone and the
  lab does not invent one.
- Status is derived (same rule as `dbo.vWorkOrderStatus`) and filtered in the database before paging.
- `import` takes every open order plus the most recent 1000 by id, and upserts by legacy id, so
  running it twice changes nothing.
- No auth yet: the stub handler arrives with the first write path (L4).
- React (Vite, TypeScript) builds into `wwwroot`, ignored by git; one port serves API and UI.

## L3 design as built

- **Outbox by triggers.** V004 adds `Production.trWorkOrderSync` and `trWorkOrderRoutingSync` (after
  insert, update, delete). Both call `sync.uspEnqueueWorkOrders`, which bumps a per-order counter in
  `sync.AggregateVersion` and writes one `sync.Outbox` row per affected order in the caller's
  transaction, so the WPF client, the procedures and any script are all captured. The row holds a
  JSON **snapshot** of the whole order (operations included), the version, and the origin from
  `SESSION_CONTEXT('sync.origin')` (default `legacy`).
- **Relay** (`src/Sync.Legacy`): polls unpublished rows in `OutboxId` order, publishes with
  publisher confirms, then marks `PublishedAt`. A crash between the two republishes the row
  (at-least-once). Message id is `legacy-<OutboxId>`.
- **Broker** (`src/Sync.RabbitMq`, behind `IMessageBus` in `src/Sync.Contracts`): direct exchange
  `legacy.events`, quorum queue `newside.workorders`, dead-letter exchange `dlx` with queue
  `newside.workorders.dlq`. A handler exception rejects the message to the dead-letter queue.
- **Consumer** (`src/WorkOrders.Api/Sync`): `WorkOrderSyncHandler` writes the change and an inbox row
  (`processed_message`) in one transaction. Already-seen id → skipped; version not newer than
  `legacy_version` → recorded and ignored; unique-violation on a concurrent duplicate → skipped.
- **Deviation from the proposed default:** the message is a whole-order snapshot, not a scrap
  delta. A snapshot makes duplicate, reorder and import-overlap safe by construction. The scrap
  delta (so scrap entered on both sides adds up) is needed once the new side can edit, so it moves
  to L4/L6.
- No retry queue yet: a failing message goes straight to the dead-letter queue (L5).

## L4 design as built

- **Writes** (`POST /api/work-orders`, `PUT …/plan`, `POST …/scrap`) go through `WorkOrderCommands`: the
  local change (optimistic, so the planner sees it at once) and a row in the PostgreSQL `outbox` commit
  together. `OutboxRelay` publishes to exchange `new.events`; the legacy side consumes `legacy.commands`.
- **Dev auth stub:** `X-Planner: <BusinessEntityID>` against the seeded list in `appsettings.json`
  (three AdventureWorks production supervisors). Anyone else gets 401. Not for production.
- **Legacy consumer** (`Sync.Legacy/LegacyCommandHandler`): one transaction holds the `sync.Inbox` row, the
  session markers (`sync.origin = 'new'`, `sync.new_id` for creates, `traceparent`) and the procedure call
  (`uspWorkOrderCreate`, `uspWorkOrderChangePlan`, `uspWorkOrderScrap`), so the AdventureWorks and lab
  triggers fire as for any other writer. The markers are cleared afterwards, because connections are pooled.
- **Echo suppression, as built.** The outbox trigger stamps `Origin = 'new'` on those rows. The relay sends
  them as `WorkOrderConfirmed`, never as `WorkOrderChanged`. The new side applies a confirmation as state and
  **writes nothing to its outbox**, so there is no loop: one command out, one confirmation back, and it ends.
  `make e2e` checks that counts stay at exactly 3 commands for 3 edits.
- **ID assignment.** The proposed separate `IdAssigned` message is folded into the first confirmation: it
  carries the new side's id (`new-id` header, `sync.Outbox.NewId`), so the new side sets `legacy_work_order_id`.
  Until then the order is *pending*; the API answers edits of a pending order with 409 and the UI hides the forms.
- **Why confirmations are sent at all** (instead of dropping `Origin = 'new'` rows): the new side applies its
  edit optimistically, and a legacy snapshot that was already in flight can overwrite it. The confirmation
  carries the legacy truth and repairs that.
- **Scrap is a delta** in the command, so scrap entered on both sides adds up.
- Conflict policy is still "last writer wins" in practice; the policy per field group is L6.

## Finding: AdventureWorks cannot change an order quantity

In the unmodified `AdventureWorks2022.bak`, `Production.uWorkOrder` inserts into `TransactionHistory` without
the NOT NULL column `ActualCost`. Any `UPDATE` of `OrderQty` or `ProductID` fails with error 515, and because
the trigger rolls back and swallows the error the caller sees only 3609 ("transaction ended in the trigger").
The desktop app could not change a quantity either. `V006__patch_uworkorder_actualcost.sql` is a labelled lab
patch (`iWorkOrder` already supplies `ActualCost = 0`). The first failing command was observed in the
dead-letter queue before the patch (`new-2 ChangePlan`). L5 needs to reproduce the unpatched behaviour as its
"trigger rolls back" case.

## L5 additions

- **Retry and dead-letter.** `RabbitMqBus` republishes a failing message to `<queue>.retry` (quorum queue,
  `x-message-ttl` = `RetryDelay`, dead-letters back to the main queue) and, after `MaxAttempts` deliveries,
  to the dead-letter queue with `x-attempt` and `x-error` headers. Defaults: 5 s, 4 deliveries. The first
  failed delivery is acknowledged only after the retry copy is published, so a crash in between redelivers
  (absorbed by the inboxes).
- The retry queues are new; the existing queues were not changed, so an already-running broker needs no reset.
