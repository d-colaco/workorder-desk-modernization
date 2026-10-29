# Conflict policy and reconciliation (L6)

## Policy, per field group

| Field group | Who wins | How it is enforced | Where a conflict is logged |
|---|---|---|---|
| Order quantity, due date | **The new side** (the moved planner desk owns planning) | The planner's `ChangePlan` command carries the quantity and due date the planner *saw*. The legacy consumer compares them with the current legacy values inside the same transaction; if they differ, the command still applies and the overwritten value is logged. On the new side, while an edit of a field is unconfirmed, an incoming legacy change to that field does not replace ours | legacy: `sync.ConflictLog` (field, base, legacy value, new value, winner). new side: `conflict_log` |
| Scrap | **No conflict** | Scrap travels as a delta (`RecordScrap`), added by the procedure; scrap entered on both sides adds up | — |
| Routing actuals | **The legacy side** (the floor records them) | The new side has no endpoint that writes routing, so there is nothing to resolve. Legacy values always arrive as state | — |
| Editing a closed order | **The legacy side** | The consumer checks before calling the procedure. A closed (or missing) order refuses the command: the refusal is logged (`Closed` / `Missing`, winner `legacy`), the command is acknowledged (not retried, not dead-lettered), and `sync.uspResync` sends the order's current state back as the command's confirmation, so the new side drops its optimistic edit | legacy `sync.ConflictLog` |

Every command carries its id (`new-<outbox id>`). The legacy outbox row it causes carries it back
(`CommandId`), and the new side stamps `ConfirmedAt` on the command. An unconfirmed command is, by definition,
in flight.

## Reconciliation report

`make reconcile` (or `GET /api/reconciliation`, `POST /api/reconciliation/run`, or the timer in the API every
`Reconcile:IntervalSeconds`, default 300) compares, per work order, every field both sides hold: product,
quantity, scrap, scrap reason, start, due and end dates, and for each operation its location, scheduled and
actual times, hours and costs (timestamps to the millisecond, decimals to 4 places). The report is also written
to `reports/` as Markdown. It covers every order the new side holds plus every legacy order that has changed
since syncing began; untouched legacy history outside the import window is not compared.

Each difference is classed:

| Class | Meaning |
|---|---|
| In flight | A change is on its way: an unpublished outbox row, an unconfirmed command, or the legacy version is ahead of what the new side applied |
| Dead-lettered | A message about that order sits in a dead-letter queue. A person must fix the cause, then `make redrive` |
| **Unexplained** | Nothing in flight, nothing dead-lettered, yet they differ. Must be zero; `make reconcile` exits 1 otherwise |

The report also lists, regardless of differences, every message in the dead-letter queues, and the conflicts
resolved (informational).

**Deviation from the plan:** the plan listed "conflict logged" as a class of difference. With this policy a
resolved conflict leaves both sides *equal* (the winner's value is applied everywhere), so it never shows as a
difference; conflicts are listed beside the differences instead.

## Limits

- Scrap flickers: a legacy snapshot already in flight can briefly hide a planner's unconfirmed scrap until the
  confirmation arrives. Eventual state is right; protecting scrap would risk double counting.
- Re-driving replays the old command as it was, including a stale one. Its conflict check still runs when it
  carries base values; commands from before L6 do not.
- The report reads both databases and the dead-letter queues one after another, so a change that commits between two
  reads can look unexplained for an instant. Since 2026-10-17 an order counts as unexplained only if it is still
  unexplained when everything is read again 2 s later; orders that cleared are named in a *Warning* line. Under a
  10 per second load, 9 of 368 reports had such an order and none ended with an unexplained row. A real inconsistency
  persists and is still reported (`A_difference_nobody_can_explain…`). The cost is one extra read, only when something
  is unexplained on the first pass.
- Not compared: product names, location names, anything not synced.
