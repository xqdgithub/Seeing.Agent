---
name: cron-management
description: Overview of scheduler cron tools and Session-bound outbound delivery (Channel/UserId on Session, not Job).
---

# Cron Management

Use these tools to manage scheduled agent jobs:

| Tool | Purpose |
|------|---------|
| `cron_list` | List all jobs (intent + next fire time) |
| `cron_create` | Create or replace an agent job |
| `cron_delete` | Permanently delete a job |
| `cron_disable` | Disable scheduling (Intent=Disabled; re-enable with resume) |
| `cron_resume` | Re-enable a disabled job |
| `cron_run` | Trigger a job once immediately |

## Session-bound outbound (important)

- When a job fires, delivery targets the **Session** bound at create time (`Dispatch.Target.SessionId`).
- **Outbound Channel / UserId live on the Session**, not on the Job.
- Do **not** put Channel or UserId on the job. There is no Job Channel fallback.
- `cron_create` binds `SessionId` from the current tool context automatically.

For create details see skill `cron-create`. For list/run see `cron-list-run`. For disable/resume/delete see `cron-lifecycle`.
