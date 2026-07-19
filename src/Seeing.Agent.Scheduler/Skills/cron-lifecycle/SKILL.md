---
name: cron-lifecycle
description: Disable, re-enable, or permanently delete scheduled jobs with cron_disable, cron_resume, and cron_delete.
---

# Cron Lifecycle

All three tools take a required `id` (job id).

| Tool | Effect |
|------|--------|
| `cron_disable` | Set Intent=Disabled; remove from scheduler (job definition kept; re-enable with `cron_resume`) |
| `cron_resume` | Set Intent=Active and re-register scheduling |
| `cron_delete` | Permanently remove the job (not recoverable) |

Prefer `cron_disable` / `cron_resume` when the job may be needed again. Use `cron_delete` only when removal is intentional. Do **not** use pause — Agent tools expose disable, not pause.
