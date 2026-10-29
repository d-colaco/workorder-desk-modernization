# Observability and measurements (L7)

## What is instrumented

One `ActivitySource` and one `Meter`, both named `DcLab.Sync`, live in `Sync.Contracts` (System.Diagnostics only).
Each process exports with the OpenTelemetry SDK (`Lab.Observability`) over OTLP/HTTP to the collector on port
32135, only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; the run targets in the Makefile set it, the tests do not.

| Process | Service name | Emits |
|---|---|---|
| API (new side, relay, consumer) | `workorder-desk-api` | ASP.NET Core requests, Npgsql, SqlClient, the sync spans, all metrics below |
| Legacy sync worker | `workorder-desk-legacy-sync` | SqlClient, the sync spans, `dclab.outbox.*` for the legacy side |
| Desktop driver (`src/Lab.Driver`) | `workorder-desk-driver` | What the WPF client does: a client span and `traceparent` in `SESSION_CONTEXT` |
| WPF client (net48) | `workorder-desk-wpf-client` | Same, from `Db.Proc`. Runs on the VM; a scrap trace starts here and crosses 3 services in 12 spans ([evidence](evidence/screenshots/trace-wpf-client.png)) |

## How one trace crosses the systems

`make trace-check` fetches two traces back from Tempo and requires every hop in the same trace:

1. **Desktop edit.** The client opens a span, puts its `traceparent` into `SESSION_CONTEXT` on the connection,
   and calls the procedure. The outbox trigger copies the context into `sync.Outbox.TraceParent`. SQL Server emits
   no spans of its own; the outbox row is the carrier. The relay records *legacy outbox wait* as a span whose
   start is the row's `CreatedAt`, relays, and publishes with the context in the `traceparent` header. The broker
   wait is recorded as *queue wait*. The consumer continues the trace, and Npgsql spans nest under *apply*.
   Result: 3 services, 14 spans.
2. **Planner edit, round trip.** The API request span → the command row stores its `traceparent` → *new outbox
   wait* → relay → publish → *queue wait* → legacy consumer → the consumer puts the context back into
   `SESSION_CONTEXT` → procedure → trigger → *legacy outbox wait* → relay → confirmation → *apply
   WorkOrderConfirmed on new side*. Result: 2 services, 29 spans, one trace id from request to confirmation.

Waits where none of our code runs (a row in an outbox, a message in a queue) are recorded as spans with their real
start time, so the gaps in the trace are visible as bars, not as blanks.

## Metrics and dashboard

Dashboard `observability/dashboards/sync.json` ("Work-order sync", provisioned into Grafana on 32134):

| Metric (Prometheus name) | Meaning |
|---|---|
| `dclab_sync_lag_seconds` (histogram, by direction) | Outbox row written → consumer commit. Recorded only when a message gets through |
| `dclab_outbox_oldest_age_seconds`, `dclab_outbox_backlog` (by side) | Age and count of unpublished outbox rows. **These show an outage building and draining**; the histogram cannot |
| `dclab_queue_depth` (by queue) | Ready messages, read by the API every 2 s; a gap means the broker could not be read |
| `dclab_sync_messages_total` (by result), `dclab_sync_relay_failures_total` | Consumed ok / retry / dead_letter; relay failures |
| `dclab_commands_unconfirmed`, `dclab_reconcile_unexplained`, `dclab_reconcile_differences` | Planner commands not yet answered; latest reconciliation |

## Measurements

Everything ran on one machine (a 12-thread desktop server with 30 GB of RAM that was also running other services, so not idle) (SQL Server, PostgreSQL, RabbitMQ, collector in Docker; API, relay and driver as
.NET processes), 2026-10-13. Lag is read **from the databases, not the dashboard**: outbox `CreatedAt` (server
clock) to the consumer's committed record (`processed_message.processed_at` or `sync.Inbox.ProcessedAt`, server
clock, stamped a few milliseconds before the commit finishes). Same host, so no clock skew. Orders are scrapped by
one unit each, 30 % of the edits through the planner API and the rest through the procedures as the desktop does.

**Steady load, default relay poll (200 ms).** `make bench ARGS="--rate 10 --seconds 30 --api-share 0.3"`:
203 desktop + 99 API edits, 0 errors.

| Path | Messages | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| legacy outbox → PostgreSQL commit | 203 | 111 | 196 | 208 | 216 |
| new outbox → SQL Server inbox | 99 | 102 | 195 | 241 | 241 |
| planner edit → confirmation back | 99 | 215 | 320 | 383 | 383 |

**Same load, relay poll 50 ms** (`Sync__PollMilliseconds=50` for `make api` and `make relay`):

| Path | Messages | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| legacy outbox → PostgreSQL commit | 203 | 34 | 58 | 60 | 79 |
| new outbox → SQL Server inbox | 99 | 27 | 54 | 93 | 93 |
| planner edit → confirmation back | 99 | 66 | 106 | 147 | 147 |

The lag floor is the relay's poll interval: the idle sleep is the largest bar in the traces (65–105 ms of
*outbox wait*), and cutting it to 50 ms cut the median by about three quarters. Polling is the cost of the outbox
design; the trade is latency against idle queries.

**Broker outage.** `make bench ARGS="--rate 5 --seconds 60 --api-share 0.3 --outage-at 15 --outage-for 15"`
(`docker compose stop rabbitmq` at 16 s, `start` at 32 s; RabbitMQ needed several more seconds to boot, which is in
the numbers). 203 desktop + 99 API edits, 0 errors, everything delivered:

| Path | Messages | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| legacy outbox → PostgreSQL commit | 203 | 154 | 19927 | 21701 | 22094 |
| new outbox → SQL Server inbox | 99 | 164 | 15886 | 19866 | 19866 |
| planner edit → confirmation back | 99 | 258 | 16710 | 20699 | 20699 |

| Changes written at | Count | p95 lag | Max lag |
|---|---|---|---|
| 0–15 s (before) | 46 | 217 ms | 217 ms |
| 15–20 s | 22 | 21.9 s | 22.1 s |
| 20–25 s | 12 | 17.0 s | 17.0 s |
| 25–30 s | 19 | 12.4 s | 12.4 s |
| 30–35 s | 14 | 7.0 s | 7.0 s |
| 35–40 s | 17 | 2.5 s | 2.5 s |
| 40 s onward | 73 | ≈ 200 ms | ≈ 210 ms |

On the dashboard's own series for the same run: the oldest unpublished row reached **20.1 s** on the legacy side
and **16.0 s** on the new side, and the backlog peaked at **69** and **27** rows; the lag histogram was empty during
the outage and jumped to 25 s p95 (over a 1-minute window) at recovery. Changes written from 40 s onward, about
8 s after the broker was started, saw normal lag again; the backlog drained in between. Nothing was lost or duplicated.

## Limits and deviations

- **Dashboard not viewed in a browser.** The JSON is provisioned (Grafana lists it) and the queries were checked
  against Prometheus through Grafana's API, but I have not looked at the rendered panels or captured a screenshot.
- **Queue depth** is a gauge the API reads from the broker, not RabbitMQ's `rabbitmq_prometheus` plugin as first
  proposed: it needs a scrape config inside the collector image and adds a moving part for no extra signal. L10's
  KEDA scaler reads RabbitMQ itself. During an outage the gauge has a gap, which is why the outbox gauges exist.
- **`make bench` is C#, not Python** (`src/Lab.Driver`): the driver has to put a trace context on a SQL
  connection, as the WPF client does, and a second language would have to re-implement the SDK. The WPF client
  cannot run on Linux, so the driver stands in for it in the measurements. The real client has run on the VM (see *Limits*).
- Lag is stamped by the consumer just before its commit completes, so it understates the true commit time by a few ms.
- One run per scenario on a shared machine; the outage figures depend on how long RabbitMQ takes to boot. Treat them as
  a demonstration of the shape, not as a benchmark of the broker.
- `grafana/otel-lgtm` v0.36.0, pinned by digest, is one dev container with no persistence guarantees and no
  production claim.
- **WPF at .NET Framework 4.8 (decision B, done):** the project retargets to net48 and carries OpenTelemetry. It
  builds on Linux and runs on the VM (2026-10-17) with no binding-redirect problem reported. The latency figures above still come
  from the driver, not from the client. A trace that starts on the VM shows about 600 ms between the client span and
  the relay's *outbox wait* that the driver's traces do not have. The VM's clock is about 0.63 s behind the host's
  (estimated from the trace and the outbox row, see verification.md), so cross-machine timings of client traces are not quoted until it is synchronised. The VM needs .NET Framework 4.8 and a route to 32135 (`make wpf-check` checks both ports).
