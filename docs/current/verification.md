# Verification (2026-10-12)

| Check | Command | Result |
|---|---|---|
| Backup checksum | `python3 scripts/get_adventureworks.py` | `d17567ad…ce5e`, 209,838,080 bytes, matches the pin |
| Restore + lab scripts | `make up` | `RESTORE DATABASE successfully processed 25378 pages`; V001–V003 applied |
| Row counts after restore | `make counts` | `Production.WorkOrder` 72,591; `WorkOrderRouting` 67,131; **open orders (`EndDate IS NULL`) 0** |
| L1 flow | `make smoke` | order 72592 went Created → Released (2 operations copied from order 72416) → Closed; 2 scrapped of 10, stocked 8; 6 audit rows; one `TransactionHistory` row of type `W` |
| Trigger behaviour | same run | History rows exist only for the insert, as the spec predicted: scrap, routing and close leave none |
| `lab_app` over the VM bind | `sqlcmd -S 192.168.122.1,32131 -U lab_app` from a container | procedure call succeeded (order 72593); direct `INSERT` into `Production.WorkOrder` denied |
| WPF build | `make wpf-build` | succeeded, 0 warnings, net45, on Linux |
| WPF run | — | done later, on the VM (2026-10-17): see *Real WPF client* below |

Consequence of the zero open orders: L2's one-off import has no open orders to take, so the
lab creates its own through the L1 procedures (`uspWorkOrderCreate`, `uspWorkOrderRelease`).

## L2

| Check | Command | Result |
|---|---|---|
| Migration + import | `make import` (twice) | `imported 1000 work orders` both times; PostgreSQL holds 1000 orders, 978 operations, 1 open |
| Parity with legacy | `make parity` | 1000 compared, 0 differences (status, quantity, scrap, due date, operation count) |
| Status filters | `curl` per status | Closed and Created return only their status; Released and In progress are empty (no such orders in the import window) |
| UI | `npm run build`; served at `/` | Builds; **not yet looked at in a browser** |

## L3

| Check | Command | Result |
|---|---|---|
| Triggers capture every step | `make smoke`, then `SELECT … FROM sync.Outbox` | create, release, 2 routing records, scrap, close gave versions 1–6 for one order, with the snapshot in each |
| Triggers work for the app login | `uspWorkOrderCreate` as `lab_app` | succeeded; the outbox row appeared (ownership chain dbo → sync holds) |
| End to end | `make api` + `make relay` + `make smoke` | 13 messages applied in order, inbox 13, outbox unpublished 0 |
| Parity after sync | `make parity` | 1003 compared, 0 differences |
| Handler tests | `make test` | 6 passed: first message, duplicate, 4 concurrent identical deliveries (1 applied, 3 duplicates), out-of-order older version ignored, newer snapshot replaces operations, delete |

Not yet covered (L5): broker down, consumer crash between commit and ack, poison message into the
dead-letter queue, trigger rollback (error 3609) on the consumer's write path, a user on the desktop
app mid-migration. Lag is not measured yet (L7).

## L4

| Check | Command | Result |
|---|---|---|
| End to end | `make e2e` (api + relay running) | create → legacy id assigned (72599); plan change and scrap reach SQL Server (qty 9, scrap 2, due 2026-12-15); audit rows carry actors 27, 40, 40; TransactionHistory has `W 4` then `W 9`; every legacy outbox row for the order is `origin = new`; the new-side outbox holds exactly 3 commands and the legacy outbox exactly 3 rows, unchanged after 3 s |
| Auth stub | `curl` without header | 401 |
| Rules | `make test` | 17 passed in all: create writes order and command together, unknown product writes nothing, edits of pending or closed orders refused, invalid scrap (zero, over quantity, unknown reason) refused without a command, scrap sent as delta, confirmation maps the legacy id without a duplicate row and without writing to the outbox, redelivered confirmation ignored, legacy truth replaces optimistic state |
| Unpatched trigger | direct `UPDATE Production.WorkOrder SET OrderQty = OrderQty` before V006 | error 515 then 3609 |
| Legacy-side handler | only through `make e2e` | no hermetic test yet (it needs a restored AdventureWorks container); L5 adds one |

## L5

| Check | Command | Result |
|---|---|---|
| Whole suite | `make test` | 34 passed in 38 s of test time (54 s wall, containers included): 17 from before, 10 legacy-side, 1 convergence, 6 broker |
| Tests can fail | mutations, each reverted | handler ignoring versions → `Out_of_order…` and `Shuffled_and_duplicated…` fail (seed 1: scrap); relay marking published before publishing → `Broker_down…` fails (expected 2 unpublished, got 1). The marker-leak test did **not** fail with the explicit clear removed (see failure-cases → Notes) |
| Outcomes per case | [failure-cases.md](failure-cases.md) | recorded there |

## L6

| Check | Command | Result |
|---|---|---|
| Whole suite | `make test` | 42 passed (1 min 10 s): 34 before, 8 new in `ReconciliationTests` |
| Report scenarios | `ReconciliationTests` | clean after ordinary work on both sides; *in flight* for an unpublished legacy change and for an unconfirmed planner edit; *dead-lettered* for a command that failed on Microsoft's trigger, and **zero differences after the V006 fix and a re-drive**; *unexplained* when a write bypasses the triggers (legacy) or goes straight into PostgreSQL (new side); crossing due-date edits log a conflict on each side, the planner wins and both sides converge; an edit of an order closed meanwhile is refused, logged and the new side gets the legacy state, with nothing dead-lettered |
| Live lab data | `make reconcile` | 1007 compared, 1007 identical, 0 unexplained. It first showed the real `ChangePlan` command left in `legacy.commands.dlq` since L4 (before the V006 patch) only after I added the dead-letter section: with no difference to attach it to, it had been invisible. `make redrive` applied it; the next report had 0 dead letters |
| Timer and endpoint | `GET /api/reconciliation` | latest report served: 1007 compared, 0 unexplained |

## L7

| Check | Command | Result |
|---|---|---|
| Traces cross the systems | `make trace-check` | desktop edit: 3 services, 14 spans, every hop present; planner round trip: 2 services, 29 spans in one trace from the HTTP request to the confirmation |
| Metrics arrive | Prometheus label values via Grafana's API | `dclab_sync_lag_seconds_*`, `dclab_outbox_*`, `dclab_queue_depth`, `dclab_sync_messages_total`, `dclab_commands_unconfirmed`, `dclab_reconcile_*` |
| Dashboard provisioned | `GET /api/search` | "Work-order sync" listed; **panels not looked at in a browser** |
| Suite unaffected | `make test` | 42 passed |
| Lag and outage | `make bench` (three runs) | numbers in [observability.md](observability.md); outage visible in both the databases' lag and the dashboard's gauges (oldest unpublished 20.1 s, backlog 69) |
| WPF net48 + OpenTelemetry | `make wpf-build` | builds; run on the VM on 2026-10-17, see *Evidence run* below |

## L8

| Check | Command | Result |
|---|---|---|
| Whole suite | `make test` | 46 passed (3 min 51 s test time on a busy host): 42 before, 4 new in `ErasureTests` |
| Erasure leaves no copy | `Erasing_a_person_leaves_no_copy…` | the scan found every collected identifier kind before (planner label, outbox payload, email table, employee row, audit table, dead-letter queue); after the erasure it found nothing in PostgreSQL, SQL Server or the six queues. Person anonymised, row and pay history kept, both logs complete, colleague untouched, second request a no-op |
| Tests can fail | mutations, each reverted | no queue redaction → `left behind: broker legacy.commands.dlq (actor reference)`; no outbox rewrite → `left behind: postgres outbox.payload.actorBusinessEntityId` |
| Redacted dead letter still works | `A_dead_lettered_command_that_named_the_person…` | after the V006 fix and a re-drive the command ran (quantity 30), its audit row says `-1`, and the scan is still empty |
| Late write | `A_late_write_that_names_an_erased_person…` | stored with `-1` |
| Minimisation | `The_new_side_and_the_broker_never_receive_names…` | the only name copies are the two `planner.display_name` values; no span tag names an actor or contains a name |
| Live lab data | `make erase ID=47` | 8 places before, 0 after; planner gone from `/api/planners`; `X-Planner: 47` gets 401 |
| Not checked | — | backups, transaction logs, WAL, the collector's stored traces: described in [erasure.md](erasure.md), not claimed |

## Simulator (`make simulate`)

| Check | Command | Result |
|---|---|---|
| Rules are legal | `dotnet test tests/Sync.Tests --filter FullyQualifiedName~SimulationTests` | 5 passed in under a second, no containers: 300 seeded orders played from create to close against the procedures' and the API's rules (release once, operations in sequence, scrap within the quantity, new quantity above what is scrapped, close only when every operation has ended), 1000 orders for the race share (20 %, ±5), and an order held open for 3000 mixed scrap and plan steps. The full suite was **not** re-run after adding these tests |
| Tests can fail | removed the scrap cap, then reverted | the long-open-order test failed (`scrap 2 on top of 24 of 25`). The first version of the tests passed with the cap removed, so it was too weak and that test was added |
| Run against the live stack | `make simulate ARGS="--per-minute 60 --seed 7"`, 4 min 42 s, stopped with SIGTERM | 44 orders closed, 11 left open, 6 close/edit races, 0 refused, 0 errors; the summary line printed on shutdown |
| Both systems still agree | `make reconcile` right after | 1127 compared, 1127 identical, **0 unexplained**, 0 messages in dead-letter queues; all six queues empty in RabbitMQ |
| Races are logged, not lost | same report | six `Closed` conflicts on the legacy side (one per race, winner `legacy`), plus new-side `OrderQty`/`DueDate` conflicts, winner `new`, on three of the same orders (a legacy snapshot of the close arriving while the planner's plan change was unconfirmed) |
| Dashboard | Playwright screenshot of "Work-order sync" | panels filled with data during the run |

Limits: the load was accelerated (60 events a minute, the default is 12), the seed fixed, and the host shared; the
numbers say the simulator and sync stayed consistent, not how fast either is. Ctrl+C was not tested from a terminal
(the harness ignores SIGINT in background jobs); SIGTERM was.

## Evidence run (2026-10-17)

*Service names:* these runs were made when the services were still called `dclab-api`, `dclab-legacy-sync`, `dclab-desktop-driver`, `dclab-wpf-client` and `dclab-simulator`; they are now `workorder-desk-api`, `workorder-desk-legacy-sync`, `workorder-desk-driver`, `workorder-desk-wpf-client` and `workorder-desk-simulator`. Rows below keep the names as they were queried. The WPF client on the VM was not redeployed, so it still reports the old name.

Quiet host, simulator off, one run per scenario unless stated. Images and how they were made: [evidence/](evidence/README.md).

| Check | Command | Result |
|---|---|---|
| Steady load, relay poll 200 ms | `make bench ARGS="--rate 10 --seconds 30 --api-share 0.3"` | 203 desktop + 99 API edits, 0 errors. legacy → PostgreSQL p50/p95/p99/max **107/197/201/209 ms** (published: 111/196/208/216); new → legacy 106/194/200/200; planner round trip 205/341/410/410 |
| Same, poll 50 ms | `Sync__PollMilliseconds=50` for `make api` and `make relay`, same bench | 0 errors. **34/57/58/68 ms** (published 34/58/60/79); new → legacy 28/54/106/106; round trip 66/105/169/169 |
| Broker outage | `make bench ARGS="--rate 5 --seconds 60 --api-share 0.3 --outage-at 15 --outage-for 15"`, three clean runs | 0 errors each. legacy → new p95 **19.9, 20.0 and 20.0 s** (published 19.9 s), max 22.0–22.2 s; broker stopped at 17 s, started at 32–33 s. Dashboard series from the first run: oldest unpublished row 19.2 s (legacy) and 15.4 s (new), backlog 64 and 26 (published 20.1 s, 16.0 s, 69, 27) |
| Run discarded | a fourth outage run | the host was overloaded while the broker restarted (it took four minutes); not counted, and the next run matches the three above |
| Trace across systems | `make trace-check` | desktop edit: 3 services, 14 spans; planner round trip: 2 services, **30 spans** (the count varies by one with the PostgreSQL spans) |
| Real WPF client | clicked on the VM, found in Tempo (`service.name = dclab-wpf-client`) | 13 traces from create, release, routing, scrap and close. A scrap trace (`52032b80…`) has 3 services, 12 spans, from the client to PostgreSQL. A refused action shows the procedure's own message in the client (`evidence/screenshots/wpf-client/06-refused-scrap-over-quantity.png`) |
| Dead letter, reproduced live | reverted `Production.uWorkOrder` to Microsoft's original (V006 minus the `ActualCost` fix), planner `PUT …/plan` raising the quantity of order 73654 from 135 to 140 | the legacy consumer failed with `Error 515 … Cannot insert the value NULL into column 'ActualCost'` (reported as 3609), retried, and the command reached `legacy.commands.dlq` after about 26 s with **4 deliveries**: the live default is `MaxAttempts` 4 with a 5 s retry delay, the tests use 3. `make reconcile`: 1188 compared, 1187 identical, order 73654 *DeadLettered*, **0 unexplained** |
| Fix and re-drive | `V006` reapplied from the repository, `make redrive`, `make reconcile` | 1 message put back, dead-letter queue 0, legacy and new side both quantity 140, **1188 of 1188 identical, 0 unexplained**. `OBJECT_DEFINITION` of the trigger is byte-identical to what was saved before the experiment |
| Full suite | `make test` | 51 passed in 2 min 11 s (46 before, plus 5 in `SimulationTests`); **52 passed** in 2 min 19 s after the reconciler re-read added one test |

**Dashboard fix found by this run.** The Prometheus datasource's minimum step is 60 s, so a 20 s outage was drawn as a
3 s hump and the backlog as 16 rows. Every panel in `observability/dashboards/sync.json` now sets `interval: 5s` (the
metrics are exported every 5 s); the dashboard then shows the 20 s and 68 rows the raw series contain.

**Reconciliation under load (found, then fixed the same day).** Among the timer reports taken while benches were running (I did not
count them), one (10:33 UTC, order 73679, scrap 0 vs 1, during the 10 per second run) classed an order as *unexplained*
with "nothing in flight"; the next report was clean and a fresh `make reconcile` found 0. The report reads the two
databases and the queues one after another, so a change committing between two reads looks unexplained for an instant,
and the dashboard's *Unexplained* stat held that 1 until the next timer run. Fix: `Reconciler` reads again after 2 s
when something is unexplained and reports only what is still unexplained, naming what cleared in a warning.
Proof: `A_difference_that_clears_between_the_two_reads…` passes, and fails with the re-read removed (the other 8
`ReconciliationTests` pass either way); live, 368 reports during a 10 per second bench (`make bench`, 0 errors, median
112 ms) gave **0 unexplained in all 368**, and 9 of them carried a "cleared on the second read" warning, which is
the false positive happening about 2.4 % of the time, now filtered. A second report taken during the overloaded run
above also showed two unexplained orders; it predates the fix and is not counted, because the system was stalled.

**Figures quoted in the article, reproduced.** 52 automated tests, 30 spans. "Eight failure cases":
the eight rows of [failure-cases.md](failure-cases.md) each have a test, all passing. "About 110 ms median, 34 ms with a
50 ms poll", "up to 22 seconds" for a 15 s outage: reproduced above.

**VM clock.** The client span of the scrap trace `52032b80…` starts at 10:10:02.267 on the VM's clock; the outbox row it
caused was stamped 10:10:02.905 by SQL Server, whose clock is the host's, within the 13 ms the span lasted. So the VM is
**0.63 s behind the host** (between −0.638 and −0.625 s), which accounts for the whole gap before *outbox wait*. Reading the VM's clock
directly over SSH failed (key login is not set up; it asks for a password), so this is an estimate from the data, good to
about ±15 ms. The same estimate on all 14 client traces of the day (10:10 to 12:15 UTC; create, release, routing, scrap, close) gives **−0.60 to −0.68 s**,
most within ±0.01 s of −0.64 s, so the offset is steady and the estimate is reliable. The last traces (12:15, taken while the
screenshots were being made) show −0.60 s, so by then the VM was **not synchronised**; it was left that way (below).

**Order 73799, both sides.** Created by a planner on the web side, quantity changed 120 → 150 by a second planner, released and then
scrapped, recorded and closed in the WPF client on the VM. The web side after the actions shows Closed, scrapped 1, stocked 149 and
both operations with the exact values typed in WPF (0.35 h / 38.20 and 0.4 h / 51.00); the client refused a scrap of 500 with the
procedure's own message. Screenshots: [evidence/](evidence/README.md).

**VM clock left 0.6 s behind.** No figure here depends on cross-machine timing of client traces.

