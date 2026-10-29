# Evidence

Everything the article shows lives here, in two folders:

- [`diagrams/`](diagrams/): the architecture, as Mermaid sources (`.mmd`, the source of truth) and their SVG renders.
- [`screenshots/`](screenshots/): the running lab, taken on 2026-10-17 (demo data only: AdventureWorks is Microsoft's public sample).
  `screenshots/wpf-client/` holds the legacy client on the Windows VM, numbered in the order the story runs.

How the numbers behind them were obtained: [../verification.md](../verification.md#evidence-run-2026-10-17).

## Diagrams

Mermaid is the source; the SVGs are renders (Mermaid 11, default theme) that prove each source parses. `01`–`03` are the article's own
blocks, verbatim; if the article changes, change them together. The alt text is for whoever renders the sources on a page.

| File | What it shows | Alt text |
|---|---|---|
| `01-before` | The legacy system: planner and floor use the WPF client, which calls stored procedures on SQL Server | A planner and the floor use a WPF client that calls stored procedures on a SQL Server database with triggers |
| `02-after` | Both systems live: WPF to SQL Server, React to the API to PostgreSQL, a legacy sync worker and the API relaying through RabbitMQ, and the reconciliation report | The old desktop path and the new web path run side by side; outboxes on each side feed RabbitMQ in both directions, and a reconciliation report reads both databases |
| `03-planner-round-trip` | One planner edit end to end: optimistic apply and command in one transaction, relay, broker, legacy consumer, trigger, relay back, confirmation | Sequence of a planner's edit from the API through PostgreSQL's outbox, RabbitMQ and the legacy consumer to SQL Server and back as a confirmation |
| `04-lab-runtime` | What runs where: the containers, the dotnet processes started with `make`, the Windows VM, and their ports | The lab on one host: SQL Server, PostgreSQL, RabbitMQ and Grafana in containers; the API, sync worker and driver as processes; the WPF client in a VM reaching SQL Server and the telemetry collector |
| `05-broker-topology` | Exchanges and queues: events one way, commands the other, each with a retry queue (TTL 5 s) and a dead-letter queue after 4 deliveries | Two flows through RabbitMQ, each with a main queue, a retry queue that returns to it and a dead-letter queue reached through one exchange |
| `06-sync-tables-legacy` | The sync tables added to SQL Server: per-order version, outbox, inbox, conflict log | Four sync tables in SQL Server: aggregate version, outbox, inbox and conflict log |
| `07-sync-tables-new` | The new side's work order, operations, outbox, processed-message and conflict tables (key columns) | The PostgreSQL work-order tables and the outbox, processed-message and conflict tables beside them |
| `08-reconciliation-classes` | How the report classifies a difference: identical, dead-lettered, in flight, or unexplained after a second read | Decision flow from a difference between the two sides to identical, dead-lettered, in flight, cleared or unexplained |

`05` uses the live defaults (`RabbitMqOptions.Default`: 4 deliveries, 5 s retry delay); the tests use 3. `06`/`07` show key columns, not
every column.

## Screenshots

Grafana, RabbitMQ and the web app were captured with headless Chromium driven by Playwright; the reconciliation reports are the
repository's own Markdown files (`src/WorkOrders.Api/reports/`) rendered to HTML so the tables read well. Nothing is edited.

| File | What it shows | Produced by |
|---|---|---|
| `trace-planner-round-trip.png` | One planner scrap, 203 ms, 31 spans across `workorder-desk-driver`, `workorder-desk-api` and `workorder-desk-legacy-sync`: HTTP request, new outbox wait and relay, broker, legacy procedure, the trigger's outbox row, relay back, confirmation applied in PostgreSQL | `make bench` (a bench trace chosen near the median), viewed in Grafana Explore → Tempo |
| `trace-desktop-edit.png` | One desktop-style edit (`make edit`): the client span, the stored procedure and `sp_set_session_context`, the outbox wait and relay, the broker and the apply into PostgreSQL: 14 spans, 3 services (`workorder-desk-driver`, `workorder-desk-legacy-sync`, `workorder-desk-api`) | `make edit`, viewed in Grafana Explore → Tempo |
| `trace-wpf-client.png` | A scrap clicked in the real WPF client on the Windows Server 2012 R2 VM: root span `workorder-desk-wpf-client`, 12 spans, 3 services, through to PostgreSQL, 1.02 s end to end. Retaken after the rename with the redeployed client. The 0.7 s gap before *legacy outbox wait* is the VM clock running about 0.7 s behind the host, not time spent | the client, then Grafana Explore → Tempo |
| `dashboard-outage-and-recovery.png` | "Work-order sync" dashboard, last 4 minutes of `make bench ARGS="--rate 5 --seconds 60 --api-share 0.3 --outage-at 15 --outage-for 15"`: oldest unpublished outbox row about 20 s (legacy) and 16 s (new), backlog about 68 and 27 rows, lag histogram empty during the outage and about 25 s p95 at recovery | `make bench`, then Grafana |
| `reconciliation-in-flight-during-outage.png` | The API's own timer report, taken while the broker was stopped: two orders *InFlight* ("legacy outbox has unpublished changes"), **0 unexplained** | the API's reconciliation timer |
| `reconciliation-dead-lettered.png` | The report after a planner quantity change was refused by Microsoft's original `uWorkOrder` trigger (error 515/3609): order 73654 *DeadLettered* with the SQL error, 0 unexplained, one message in the dead-letter queue. In both reports the legacy values are the legacy server's time, which has no zone (an hour ahead of UTC that day); the *When* column is UTC | `make reconcile` after the experiment in [verification.md](../verification.md#evidence-run-2026-10-17) |
| `rabbitmq-dead-letter-queue.png` | RabbitMQ's queue list at the same moment: `legacy.commands.dlq` holds 1 message, everything else 0 | RabbitMQ management UI |
| `web-orders-list.png`, `web-order-detail.png` | The React planner desk: statuses and quantities, and an in-progress order's operations. The list is filtered to closed orders, with the demo order 73799 at the top | `make api` |

**Synthetic traffic.** Orders in these images that were not created by hand come from `make bench` or `make simulate`
(service `workorder-desk-simulator`, span tag `lab.synthetic=true`). Rates and counts in the images are not measurements; the
article's figures come from the `make bench` tables in [../observability.md](../observability.md).

### The demo order 73799, both sides

A planner created it on the web side and a second planner changed its quantity 120 → 150; it was then released, scrapped, recorded and
closed in the WPF client on the VM. The web screenshots are taken with `make api`; the WPF ones are in `screenshots/wpf-client/`.

| File | Shows |
|---|---|
| `wpf-client/01-list-released.png` | The floor's list filtered to *Released*: the orders ready to start, each with its quantity and due date (73799's own Released state is in `02`) |
| `wpf-client/02-detail-released.png` | Its detail window before any action: two operations, scrapped 0. Web pair: `web-order-73799-before-actions.png` |
| `wpf-client/03-scrap-dialog.png` | The Record scrap dialog with 1 entered |
| `wpf-client/04-detail-after-scrap.png` | The detail with scrapped 1 |
| `wpf-client/05-routing-recorded.png` | Operation 6 recorded (11:05 to 11:25, 0.35 h, 38.20): status *In progress*, the fields still showing what was typed |
| `wpf-client/06-refused-scrap-over-quantity.png` | The refusal "Scrap would exceed the order quantity." for 500: the stored procedure's rule, shown by the unchanged client |
| `wpf-client/07-detail-closed.png` | Closed, both operations recorded. Web pair: `web-order-73799-after-actions.png` (Closed, scrapped 1, stocked 149, same hours and costs) |
| `wpf-client/08-list-closed.png` | The list afterwards, filtered to *Closed*: 73799 at the top, end date 11:55 (the last operation's end) |
| `wpf-client/09-list-all-statuses.png` | The unfiltered list: orders in progress and closed side by side, as the floor sees them |
| `web-order-73799-before-actions.png`, `web-order-73799-after-actions.png` | The same order on the web side, before and after the desktop actions |

The VM's clock runs about 0.6 s behind the host (disclosed in the article); client traces are not used for cross-machine timings.
