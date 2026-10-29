# Current state

**L0–L9 done, including the real WPF client run on a Windows Server 2012 R2 VM (2026-10-17).**

| Doc | Holds |
|---|---|
| [status.md](status.md) | What exists, per slice, and what is not done |
| [runbook.md](runbook.md) | Commands: bring-up, reset, SQL shell, WPF build and deploy |
| [windows-vm.md](windows-vm.md) | The Windows Server 2012 R2 VM that runs the WPF client: what it needs and why it takes more than installing Windows |
| [verification.md](verification.md) | What was checked, with the commands and numbers |
| [failure-cases.md](failure-cases.md) | Each failure case: test, observed outcome, rule |
| [evidence/](evidence/README.md) | Diagrams (Mermaid sources and SVG) and screenshots for the article: traces, dashboard through an outage, reconciliation reports, queues, the WPF client |
| [erasure.md](erasure.md) | GDPR erasure: where a person appears, the flow, what stays, how it is checked, backups described |
| [observability.md](observability.md) | Tracing and metrics, the dashboard, and the measurements with the commands that produced them |
| [conflicts-and-reconciliation.md](conflicts-and-reconciliation.md) | Conflict policy per field group, and the reconciliation report |

Architecture so far is one container: SQL Server 2022 with AdventureWorks2022 restored from
Microsoft's backup, plus the lab's own versioned scripts in `legacy/sql/`. The WPF client is
`legacy/wpf/WorkOrderDesk` (.NET Framework 4.5, SQL in code-behind, procedures
`dbo.uspWorkOrder*`).
