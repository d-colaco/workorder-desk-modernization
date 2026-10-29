#!/usr/bin/env python3
"""L2 check: every work order the API serves matches dbo.vWorkOrderStatus on the legacy side
(status, quantities, scrap, dates) and its operation count matches Production.WorkOrderRouting."""
import json
import subprocess
import sys
import urllib.request

API = "http://127.0.0.1:32130/api/work-orders?take=500&skip={}"


def legacy_rows(ids):
    sql = ("SET NOCOUNT ON; SELECT v.WorkOrderID, v.Status, v.OrderQty, v.ScrappedQty, "
           "CONVERT(char(10), v.DueDate, 23), (SELECT COUNT(*) FROM Production.WorkOrderRouting r "
           "WHERE r.WorkOrderID = v.WorkOrderID) FROM dbo.vWorkOrderStatus v WHERE v.WorkOrderID IN ("
           + ",".join(map(str, ids)) + ")")
    out = subprocess.run(
        ["docker", "compose", "exec", "-T", "mssql", "bash", "-c",
         '/opt/mssql-tools18/bin/sqlcmd -C -d AdventureWorks2022 -U sa -P "$MSSQL_SA_PASSWORD" '
         '-h -1 -W -s "|" -Q "$0"', sql],
        capture_output=True, text=True, check=True).stdout
    rows = {}
    for line in out.splitlines():
        p = line.split("|")
        if len(p) == 6:
            rows[int(p[0])] = (p[1], int(p[2]), int(p[3]), p[4], int(p[5]))
    return rows


def main() -> int:
    api, skip = [], 0
    while True:
        page = json.load(urllib.request.urlopen(API.format(skip)))
        api += page
        if len(page) < 500:
            break
        skip += 500
    legacy = legacy_rows([w["legacyWorkOrderId"] for w in api])
    bad = 0
    for w in api:
        got = (w["status"], w["orderQty"], w["scrappedQty"], w["dueDate"][:10], len(w["operations"]))
        want = legacy.get(w["legacyWorkOrderId"])
        if got != want:
            bad += 1
            print("DIFF", w["legacyWorkOrderId"], "api", got, "legacy", want)
    print(f"compared {len(api)} work orders, {bad} differences")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
