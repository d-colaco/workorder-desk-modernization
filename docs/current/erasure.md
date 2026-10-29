# GDPR erasure across both systems (L8)

The scenario: an employee (an AdventureWorks person, fictional) has acted in the lab as a planner or on the floor.
They ask to be erased. The legacy system owns the person data; the new side holds a copy of their label and a
reference to them. This page covers what is stored where, what an erasure does, what stays, and how it is checked.
It is a technical description for the lab, not legal advice.

## Where a person appears

| Place | What | Minimised? |
|---|---|---|
| SQL Server `Person.*`, `HumanResources.Employee` (AdventureWorks) | name, email, phone, address, password hash, national id, login, birth date, job title | Owner of the data; not copied |
| SQL Server `dbo.WorkOrderAudit` (lab) | the acting employee's `BusinessEntityID` per change | A reference, no name |
| PostgreSQL `planner` | `business_entity_id` and a display label (the person's name) for the UI | The **only** name on the new side |
| PostgreSQL `outbox.payload` | `actorBusinessEntityId` inside planner commands | A reference, no name |
| Broker queues and dead-letter queues | the same command payloads | A reference, no name |
| Sync messages about work orders | order state only; no actor, no names | Nothing personal |
| Spans, metrics, logs | no actor, no names (tested); erasure messages carry no subject id in spans or log lines | Nothing personal |

`ErasureTests.The_new_side_and_the_broker_never_receive_names…` checks the minimisation claim: after a realistic
week of work, the only place on the new side or in the broker where a planner's name occurs is
`planner.display_name`, and no span tag names an actor or contains a name.

## The flow

1. `make erase ID=<BusinessEntityID>` (or `ErasureService.EraseAsync`) records the request in `erasure_log`, marks the
   planner erased (label cleared, can no longer sign in) and waits for that person's commands already in flight to be
   answered. Commands stuck in a dead-letter queue are not waited for.
2. It sends an `EraseSubject` command to the legacy side through the ordinary outbox, broker and consumer.
3. `dbo.uspEraseSubject` runs in one transaction with the consumer's inbox row:
   - `Person.Person`: names, title, suffix, contact info and demographics replaced or cleared;
   - `Person.EmailAddress`, `Person.PersonPhone`, `Person.Password`, `Person.BusinessEntityAddress` rows deleted, and
     the `Person.Address` row too unless something else uses it;
   - `HumanResources.Employee`: national id, login, job title, birth date, marital status and gender replaced by
     placeholders (not true values); `JobCandidate.Resume` cleared;
   - `dbo.WorkOrderAudit.ActorBusinessEntityID` set to the tombstone `-1` for their rows, and `dbo.ErasedSubject`
     records them so that **a later write naming them is also stored as `-1`** (a late command, a script);
   - `dbo.ErasureLog` gets a row (request id, subject id, time, row counts), and a `SubjectErased` event is queued.
4. The relay sends the event. The new side's handler clears the planner label, rewrites `actorBusinessEntityId` to
   `-1` in every outbox payload, and completes its `erasure_log` row.
5. The service redacts messages still sitting in the queues (retry and dead-letter queues in particular): each is
   taken, its actor set to `-1`, and put back, so a dead-lettered command can still be re-driven later.

Both erasure logs hold ids, timestamps and counts and **no personal data**. They keep the subject's
`BusinessEntityID`: after the erasure that number no longer identifies anyone, and the log is the evidence the
request was honoured.

## What stays, and why

| Kept | Why |
|---|---|
| The `Person` and `Employee` rows | Payroll, purchasing and sales rows point at them. They now hold placeholders only |
| `EmployeePayHistory`, hire date, vacation and sick-leave hours | Whether payroll records must be kept is a legal question (retention duties), not a technical one; this lab does not decide it |
| The audit rows themselves | They record what happened and when, without who |
| The `-1` tombstone | A marker for "a person who has been erased" |

## How it is checked

`ErasureVerifier` searches for the person everywhere the lab stores or moves data, and reports the **kind** of
identifier and where, never the value:

- every text, JSON and XML column of every PostgreSQL table;
- every char, varchar, text, nchar, nvarchar, ntext and xml column of every SQL Server table, including all of
  AdventureWorks (names are searched as one full string; email, national id, login, phone and street address as
  themselves; a value that also occurs inside another person's data is left out, because finding it afterwards
  would prove nothing about the subject);
- the audit table and outbox payloads for the actor reference;
- the headers and bodies of every message in all six queues.

It is first run **before** the erasure, as a positive control: the test requires it to find every kind of identifier
it collected (planner label, outbox payload, email table, employee row, audit table, dead-letter queue, and so on).
Only then does the zero after the erasure mean something. Two mutations were tried (skip the queue redaction; skip
the outbox rewrite) and each made the test fail with the exact leftover.

Other tests: the erasure is idempotent; a dead-lettered command that named the person still runs after redaction
and leaves no trace of them; a late write that names an erased person is stored without them.

A live run on the lab data (`make erase ID=47`, planner Andrew Hill): 8 places held the person before, 0 after; the
planner list no longer shows them and their `X-Planner` header gets 401.

## Backups and other copies: described, not claimed

- `data/AdventureWorks2022.bak` is Microsoft's original download and still contains the original (fictional) people.
  An erasure does not and cannot change it. In a real system, a backup would be kept for its retention period and
  the erasures replayed after any restore; `dbo.ErasedSubject` and the two erasure logs are what you would replay from.
- SQL Server's transaction log and PostgreSQL's write-ahead log and dead tuples hold earlier versions of rows until
  they are truncated or vacuumed. The lab does not force that and does not claim the old values are gone from disk.
- The Docker volumes, container images and the host's own backups are outside what the lab controls.
- Exported traces and metrics sit in the collector (`grafana/otel-lgtm`), which the check does not search. The lab's
  spans carry no names and no actor (tested), and erasure messages carry no subject id in spans or log lines.
- Reconciliation reports in `reports/` contain order data only.

## Limits

- The check searches for strings it can predict. A copy in another form (a screenshot, an export someone made, a
  differently spelled name) is out of its reach.
- A person who is not an employee (a customer or vendor contact) would need the same treatment of different tables;
  only the employee case is built.
- There is no authorisation on the erase command beyond being able to run it; a real system would verify the data
  subject's identity and the operator's role first.
- The scan reads every table, so `make test` now takes about four minutes on a busy host.
