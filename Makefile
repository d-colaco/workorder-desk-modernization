# Lab commands. Microsoft's .NET 10 SDK is installed user-locally (see docs/current/runbook.md).
DOTNET ?= $(HOME)/.dotnet-sdk10/dotnet
export DOTNET_ROOT ?= $(HOME)/.dotnet-sdk10
VM_HOST ?= dclab-vm

-include .env
# Spans and metrics go to the collector on 32135 (docker compose up lgtm). Set only for the run targets, not for tests.
OTEL_ENV = OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:32135 OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf OTEL_METRIC_EXPORT_INTERVAL=5000
export POSTGRES_PASSWORD LAB_APP_PASSWORD RABBITMQ_PASSWORD
export RabbitMq__Uri = amqp://lab:$(RABBITMQ_PASSWORD)@127.0.0.1:32136/
# Connection strings for the API when run from the working tree (values come from .env)
export ConnectionStrings__Postgres = Host=127.0.0.1;Port=32132;Database=workorders;Username=lab;Password=$(POSTGRES_PASSWORD)
export ConnectionStrings__LegacySql = Server=127.0.0.1,32131;Database=AdventureWorks2022;User Id=lab_app;Password=$(LAB_APP_PASSWORD);TrustServerCertificate=true

.PHONY: setup up down reset sql counts smoke erase migrate import api relay web parity edit bench simulate trace-check test e2e reconcile redrive wpf-build wpf-deploy wpf-check

setup:                  ## copy .env and download the AdventureWorks backup (checksum pinned)
	@python3 scripts/make_env.py
	python3 scripts/get_adventureworks.py

up: setup               ## SQL Server with AdventureWorks restored and the lab scripts applied
	docker compose up -d --wait mssql
	docker compose run --rm mssql-init

down:
	docker compose down

reset:                  ## drop the database volume; next `make up` restores from the .bak
	docker compose down -v

sql:                    ## interactive sqlcmd against AdventureWorks2022
	docker compose exec mssql bash -c '/opt/mssql-tools18/bin/sqlcmd -C -d AdventureWorks2022 -U sa -P "$$MSSQL_SA_PASSWORD"'

counts:                 ## the numbers recorded in docs/current/verification.md
	docker compose exec mssql bash -c '/opt/mssql-tools18/bin/sqlcmd -C -d AdventureWorks2022 -U sa -P "$$MSSQL_SA_PASSWORD" -Q "SELECT (SELECT COUNT(*) FROM Production.WorkOrder) AS WorkOrders, (SELECT COUNT(*) FROM Production.WorkOrderRouting) AS Routings, (SELECT COUNT(*) FROM Production.WorkOrder WHERE EndDate IS NULL) AS OpenOrders"'

smoke:                  ## runs the L1 procedures end to end (creates one work order)
	docker compose exec -T mssql bash -c '/opt/mssql-tools18/bin/sqlcmd -C -I -b -d AdventureWorks2022 -U sa -P "$$MSSQL_SA_PASSWORD"' < legacy/tests/smoke_work_order_flow.sql

migrate:                ## apply EF Core migrations to PostgreSQL
	$(DOTNET) run --project src/WorkOrders.Api -- migrate

import: migrate         ## one-off import: open orders + the most recent 1000 from the legacy database
	$(DOTNET) run --project src/WorkOrders.Api -- import 1000

relay:                  ## legacy outbox relay: sync.Outbox -> RabbitMQ
	$(OTEL_ENV) $(DOTNET) run --project src/Sync.Legacy

test:                   ## xUnit with Testcontainers (needs Docker)
	TESTCONTAINERS_RYUK_DISABLED=true $(DOTNET) test tests/Sync.Tests

e2e:                    ## L4 end to end (api and relay running): create, plan, scrap via the API, checked in SQL Server
	python3 scripts/e2e_l4.py

reconcile:              ## compare both sides per work order; exits 1 if anything is unexplained
	$(DOTNET) run --project src/WorkOrders.Api -- reconcile

redrive:                ## put dead-lettered messages back on their queues (after fixing the cause)
	$(DOTNET) run --project src/WorkOrders.Api -- redrive

edit:                   ## one traced desktop-style edit (what the WPF client does), prints the trace id
	$(OTEL_ENV) $(DOTNET) run --project src/Lab.Driver -- edit

trace-check:            ## one desktop edit and one planner edit; fetch both traces from Tempo and require every hop
	python3 scripts/check_trace.py

bench:                  ## fixed-rate load, lag read from the databases; ARGS="--rate 5 --seconds 60 --api-share 0.3 --outage-at 20 --outage-for 15"
	$(OTEL_ENV) $(DOTNET) run --project src/Lab.Driver -- bench $(ARGS)

simulate:               ## synthetic background activity until Ctrl+C (api and relay running): whole order lifecycles, desktop and planner; ARGS="--per-minute 12 --max-open 12 --seed 0"
	$(OTEL_ENV) $(DOTNET) run --project src/Lab.Driver -- simulate $(ARGS)

erase:                  ## erase a person across both systems and the broker, then scan for what is left: make erase ID=78 (api and relay running)
	$(DOTNET) run --project src/WorkOrders.Api -- erase $(ID)

web:                    ## builds React into the API's wwwroot
	cd src/WorkOrders.Web && npm install && npm run build

api: web                ## API + React on http://127.0.0.1:32130
	$(OTEL_ENV) ASPNETCORE_ENVIRONMENT=Development $(DOTNET) run --project src/WorkOrders.Api

parity:                 ## L2 check: API (running) vs the legacy status view
	python3 scripts/parity_l2.py

wpf-build:              ## builds the net48 WPF client on Linux
	$(DOTNET) build legacy/wpf/WorkOrderDesk.slnx -c Release

# wpf-deploy and wpf-check need the VM set up as in docs/current/windows-vm.md.
wpf-deploy: wpf-build   ## copy the client to the VM with the lab_app connection string
	scripts/wpf-deploy.sh $(VM_HOST)

wpf-check:              ## prove the VM reaches SQL Server on 32131
	scripts/wpf-check.sh $(VM_HOST)
