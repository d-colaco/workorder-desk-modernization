#!/usr/bin/env python3
"""L7 check (needs `make up`, `make api`, `make relay`): make one traced desktop edit, one traced planner edit and
fetch each trace back from Tempo (through Grafana's datasource proxy). Fails unless every expected hop is present
in the SAME trace, across processes."""
import json
import os
import re
import subprocess
import sys
import time
import urllib.request

GRAFANA = "http://127.0.0.1:32134/api/datasources/proxy/uid/tempo/api/traces/"
API = "http://127.0.0.1:32130/api"
ENV = dict(os.environ, OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:32135", OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf")


def fetch(trace_id, want, timeout=40):
    end = time.time() + timeout
    while time.time() < end:
        try:
            d = json.load(urllib.request.urlopen(GRAFANA + trace_id))
        except Exception:
            time.sleep(1)
            continue
        spans = []
        for b in d.get("batches", []):
            svc = next(a["value"]["stringValue"] for a in b["resource"]["attributes"] if a["key"] == "service.name")
            for ss in b["scopeSpans"]:
                for s in ss["spans"]:
                    spans.append((int(s["startTimeUnixNano"]), svc, s["name"], (int(s["endTimeUnixNano"]) - int(s["startTimeUnixNano"])) / 1e6,
                                  s["spanId"], s.get("parentSpanId", "")))
        names = " | ".join(s[2] for s in spans)
        if all(re.search(w, names) for w in want):
            return sorted(spans)
        time.sleep(1)
    raise SystemExit(f"FAIL: trace {trace_id} never showed {want}; got {sorted({s[2] for s in spans}) if 'spans' in dir() else 'nothing'}")


def show(spans):
    t0, ids = spans[0][0], {s[4]: s for s in spans}
    for st, svc, name, dur, sid, par in spans:
        depth, p = 0, par
        while p in ids:
            depth, p = depth + 1, ids[p][5]
        print(f"  {(st - t0) / 1e6:8.1f} ms {dur:8.1f} ms  {'  ' * depth}{name}  [{svc}]")
    print(f"  {len({s[1] for s in spans})} services, {len(spans)} spans")


def check(label, spans, expect_services):
    got = {s[1] for s in spans}
    missing = expect_services - got
    print(f"ok   {label}: services {sorted(got)}" if not missing else f"FAIL {label}: missing services {sorted(missing)}")
    if missing:
        sys.exit(1)


# 1. desktop edit: client span -> SESSION_CONTEXT -> trigger -> outbox -> relay -> broker -> consumer -> PostgreSQL
out = subprocess.run(["make", "edit"], env=ENV, capture_output=True, text=True).stderr
tid = re.findall(r"scrap: trace ([0-9a-f]{32})", out)[-1]
spans = fetch(tid, [r"WorkOrderDesk scrap", r"sp_set_session_context", r"legacy outbox wait", r"legacy outbox relay", r"publish legacy\.events",
                    r"queue wait newside\.workorders", r"consume newside\.workorders", r"apply WorkOrderChanged on new side", r"postgresql"])
print("desktop edit:"); show(spans)
check("desktop edit trace", spans, {"workorder-desk-driver", "workorder-desk-legacy-sync", "workorder-desk-api"})

# 2. planner edit: request -> outbox -> relay -> broker -> legacy procedure -> trigger -> outbox -> relay -> confirmation -> PostgreSQL
orders = json.load(urllib.request.urlopen(API + "/work-orders?take=1"))
oid = orders[0]["id"]
tp = "00-%s-%s-01" % (os.urandom(16).hex(), os.urandom(8).hex())
req = urllib.request.Request(API + f"/work-orders/{oid}/scrap", method="POST", data=json.dumps({"quantity": 1, "reasonId": 7}).encode(),
                             headers={"content-type": "application/json", "X-Planner": "27", "traceparent": tp})
try:
    urllib.request.urlopen(req)
except urllib.error.HTTPError as e:  # an order that cannot take more scrap still proves nothing; use a fresh one
    sys.exit(f"FAIL: planner edit refused: {e.read()}")
tid2 = tp.split("-")[1]
spans2 = fetch(tid2, [r"POST", r"new outbox wait", r"new outbox relay", r"publish new\.events", r"consume legacy\.commands",
                      r"apply RecordScrap on legacy", r"legacy outbox wait", r"consume newside\.workorders", r"apply WorkOrderConfirmed on new side"])
print("planner edit, round trip:"); show(spans2)
check("planner round-trip trace", spans2, {"workorder-desk-api", "workorder-desk-legacy-sync"})
print("trace check passed")
