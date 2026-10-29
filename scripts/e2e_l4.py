#!/usr/bin/env python3
"""L4 end to end (needs `make up`, `make api`, `make relay` running): a planner's create, plan change and scrap
through the API reach SQL Server via the stored procedures, leave history and audit rows, come back as
confirmations, and stop there (no echo loop)."""
import json
import subprocess
import sys
import time
import urllib.request

API = "http://127.0.0.1:32130/api"


def call(method, path, body=None, planner=40):
    req = urllib.request.Request(API + path, method=method, data=json.dumps(body).encode() if body else None,
                                 headers={"content-type": "application/json", "X-Planner": str(planner)})
    with urllib.request.urlopen(req) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def sql(query):
    out = subprocess.run(["docker", "compose", "exec", "-T", "mssql", "bash", "-c",
                          '/opt/mssql-tools18/bin/sqlcmd -C -d AdventureWorks2022 -U sa -P "$MSSQL_SA_PASSWORD" -h -1 -W -s "|"'],
                         input="SET NOCOUNT ON;" + query, capture_output=True, text=True, check=True).stdout
    return [l.split("|") for l in out.strip().splitlines() if l.strip()]


def psql(query):
    return subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "lab", "workorders", "-tAc", query],
                          capture_output=True, text=True, check=True).stdout.strip()


def wait(cond, what, timeout=15):
    end = time.time() + timeout
    while time.time() < end:
        v = cond()
        if v:
            return v
        time.sleep(0.3)
    raise SystemExit(f"FAIL: timed out waiting for {what}")


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what)
    if not ok:
        sys.exit(1)


products = call("GET", "/products")
product = next(p for p in products if p["id"] == 722) if any(p["id"] == 722 for p in products) else products[0]
cmds_before = int(psql("select count(*) from outbox"))

created = call("POST", "/work-orders", {"productId": product["id"], "orderQty": 4, "dueDate": "2026-12-01"}, planner=27)
oid = created["id"]
check(call("GET", f"/work-orders/{oid}")["legacyWorkOrderId"] is None, "new order starts pending (no legacy id yet)")
legacy_id = wait(lambda: call("GET", f"/work-orders/{oid}")["legacyWorkOrderId"], "legacy id assigned")
check(sql(f"SELECT OrderQty FROM Production.WorkOrder WHERE WorkOrderID={legacy_id}") == [["4"]], f"legacy order {legacy_id} exists with qty 4")

call("PUT", f"/work-orders/{oid}/plan", {"orderQty": 9, "dueDate": "2026-12-15"}, planner=40)
call("POST", f"/work-orders/{oid}/scrap", {"quantity": 2, "reasonId": 7}, planner=40)
wait(lambda: sql(f"SELECT OrderQty, ScrappedQty, CONVERT(char(10),DueDate,23) FROM Production.WorkOrder WHERE WorkOrderID={legacy_id}") == [["9", "2", "2026-12-15"]],
     "legacy order to show qty 9, scrap 2, due 2026-12-15")
check(True, "legacy order shows the planner's edits")

audit = sql(f"SELECT Action, ActorBusinessEntityID FROM dbo.WorkOrderAudit WHERE WorkOrderID={legacy_id} ORDER BY AuditID")
check(audit == [["Create", "27"], ["ChangePlan", "40"], ["Scrap", "40"]], f"audit rows carry the actors: {audit}")
hist = [r for r in sql(f"SELECT TransactionType, Quantity FROM Production.TransactionHistory WHERE ReferenceOrderID={legacy_id} AND TransactionType='W' ORDER BY TransactionID")]
check(hist == [["W", "4"], ["W", "9"]], f"AdventureWorks triggers wrote history rows: {hist}")
origins = sql(f"SELECT DISTINCT Origin FROM sync.Outbox WHERE AggregateId={legacy_id}")
check(origins == [["new"]], "every legacy outbox row for it is marked origin=new")

wait(lambda: (lambda w: w["orderQty"] == 9 and w["scrappedQty"] == 2)(call("GET", f"/work-orders/{oid}")), "confirmation back on the new side")
wait(lambda: sql("SELECT COUNT(*) FROM sync.Outbox WHERE PublishedAt IS NULL") == [["0"]], "legacy outbox drained")
time.sleep(3)
check(int(psql("select count(*) from outbox")) == cmds_before + 3, "new-side outbox holds exactly the 3 commands: no echo")
check(sql(f"SELECT COUNT(*) FROM sync.Outbox WHERE AggregateId={legacy_id}") == [["3"]], "legacy outbox holds exactly 3 rows for it, and stays at 3")
print("L4 end to end: all checks passed")
