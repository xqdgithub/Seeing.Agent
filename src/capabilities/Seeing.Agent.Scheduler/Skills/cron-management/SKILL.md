---
name: cron-management
description: Overview of scheduler cron tools and Session-bound outbound delivery (Channel/UserId on Session, not Job).
---

# Cron Management

Use these tools to manage scheduled jobs (text reminders or agent runs):

| Tool | Purpose |
|------|---------|
| `cron_list` | List all jobs (intent + next fire time) |
| `cron_create` | Create or replace a job (required taskType: text\|agent) |
| `cron_delete` | Permanently delete a job |
| `cron_disable` | Disable scheduling (Intent=Disabled; re-enable with resume) |
| `cron_resume` | Re-enable a disabled job |
| `cron_run` | Trigger a job once immediately |

## Session-bound outbound (important)

- When a job fires, delivery targets the **Session** bound at create time (`Dispatch.Target.SessionId`).
- **Outbound Channel / UserId live on the Session**, not on the Job.
- Do **not** put Channel or UserId on the job. There is no Job Channel fallback.
- `cron_create` binds `SessionId` from the current tool context automatically.

Prefer `text` for pure reminders; `agent` only when work must run at fire time. See skill `cron-create`.

For create details (including interval `windows`) see skill `cron-create`. For list/run see `cron-list-run`. For disable/resume/delete see `cron-lifecycle`.
