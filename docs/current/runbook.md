# Runbook

## Prerequisites

- Docker with Compose, Python 3, about 2 GB free RAM for SQL Server.
- Microsoft's .NET 10 SDK, user-local (Ubuntu's package cannot build WPF):
  `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet-sdk10`.
  The Makefile uses `~/.dotnet-sdk10/dotnet`; `global.json` pins 10.0.401.

## Database

```bash
make up        # copies .env.example to .env, downloads the .bak (SHA-256 pinned), starts SQL Server,
               # restores AdventureWorks2022, applies legacy/sql/V*.sql once each (tracked in dbo.LabMigration)
make counts    # work orders, routings, open orders
make smoke     # create, release, record, scrap, close one work order through the procedures
make sql       # interactive sqlcmd
make reset     # drop the volume; next `make up` restores from the .bak
```

SQL Server listens on host port 32131, bound to `127.0.0.1` and to `192.168.122.1` (libvirt
bridge, for the Windows VM). Logins: `sa` (admin only) and `lab_app` (client and workers).
Passwords are in `.env` (untracked; template in `.env.example`).

The first `docker compose` run pulls about 1.5 GB from Microsoft's registry; on this host it
stalled once and succeeded on a retry.

New legacy scripts: add `legacy/sql/V00N__name.sql`; `make up` applies the new ones. Scripts
already applied are never re-run, so change history means a new script.

## New side

```bash
make up        # also starts PostgreSQL (32132), RabbitMQ (32136, UI 32133) and the telemetry backend (Grafana 32134, OTLP 32135)
make import    # applies EF migrations, then the one-off legacy import (idempotent)
make api       # builds React, serves API + UI on http://127.0.0.1:32130
make relay     # in another terminal: legacy outbox -> RabbitMQ (UI at http://127.0.0.1:32133, user lab)
make parity    # with the API running: compares it to the legacy status view
make reconcile # per-order comparison of both sides + dead letters + conflicts; reports/ ; exits 1 if unexplained
make redrive   # put dead-lettered messages back on their queues (fix the cause first)
make erase ID=78 # erase a person across both systems and the broker, then scan for what is left (api and relay running)
make e2e       # L4 end to end against the running stack
make trace-check # one desktop edit and one planner edit; fetches both traces from Tempo and requires every hop
make edit      # one traced desktop-style edit (stands in for the WPF client)
make bench ARGS="--rate 10 --seconds 30 --api-share 0.3"   # lag percentiles from the databases; add --outage-at 15 --outage-for 15 to stop the broker meanwhile
make simulate  # synthetic background activity until Ctrl+C (api and relay running); ARGS="--per-minute 12 --max-open 12 --seed 0"
make test      # 46 tests, ~4 min (the erasure tests scan every table); needs Docker and `make setup` (the .bak); starts its own SQL Server, PostgreSQL and RabbitMQ
```

`make simulate` keeps the dashboard and traces looking like a working desk. It takes whole orders through their life,
mixing the desktop procedures and the planner API: create, release, routing start and finish per operation, scrap,
plan changes and close, with now and then a burst of events. One close in five is raced against a planner edit, the
one conflict a desktop user can really cause (the legacy side refuses the edit, logs it as `Closed` and sends its state
back). It stops cleanly on Ctrl+C or SIGTERM; orders still open then stay open, and a new run starts from an empty
pool. **It is synthetic**: it reports as service `workorder-desk-simulator` and tags its spans `lab.synthetic=true`, and nothing
it produces (rates, lags, counts) may be quoted as a measurement. The article's figures come from `make bench`.
The rules it follows are in `src/Lab.Driver/Simulation.cs`, tested without containers in `SimulationTests`.

The API also runs the reconciliation every 5 minutes (`Reconcile:IntervalSeconds`, 0 disables) and serves the latest at `GET /api/reconciliation`.

Order: `make up`, `make import`, then `make api` and `make relay`. Planner writes (the React forms, or `curl -H 'X-Planner: 27' …`) go the other way. Edits made with `make smoke` (or the
WPF client) reach PostgreSQL within about a second; the API log prints each applied message.

New migration: `dotnet tool restore && dotnet ef migrations add <Name> --project src/WorkOrders.Api --output-dir Migrations`.
Connection strings are built in the Makefile from `.env`. The API itself is not in Compose yet.

## WPF client

```bash
make wpf-build    # Linux, net48 → legacy/wpf/WorkOrderDesk/bin/Release/net48/
make wpf-deploy   # scp to the VM (host alias dclab-vm), writes the connection string from .env
make wpf-check    # proves the VM reaches SQL Server (32131) and the collector (32135)
```

`wpf-deploy` and `wpf-check` need the one-time VM setup in [windows-vm.md](windows-vm.md). Then start the app from
the VM desktop (`virsh start win2k12r2`, RDP or virt-viewer) and run C:\Lab\wpf\WorkOrderDesk.exe.

## Telemetry

Grafana: <http://127.0.0.1:32134> (anonymous admin in this dev image), dashboard *Work-order sync*. `make api`,
`make relay`, `make edit` and `make bench` export to the collector; set `Sync__PollMilliseconds` (default 200)
for the relays to change the lag floor.
