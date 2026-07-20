---
name: cron-create
description: Create or replace scheduled jobs with cron_create. Required taskType text|agent; SessionId-bound.
---

# Cron Create

Use tool `cron_create` to create or replace a scheduled job.

## When to choose taskType

| taskType | Use when |
|----------|----------|
| `text` | Reminder / notification only — deliver a fixed message (e.g. "time to sleep"). No reasoning, research, or tools at fire time. |
| `agent` | Must run an Agent at fire time — summarize, check, report, or use tools. Content depends on current context. |

**Bias:** If the user only wants "tell me X at time T", choose `text`. Choose `agent` only when the Agent must *do work* at fire time.

- `text` `prompt` = the exact message the user will see
- `agent` `prompt` = instructions for the Agent (not user-facing copy)

## Parameters

- `taskType` (required) — `text` | `agent`
- `prompt` (required) — see above
- `schedule` (required) — object with `type` = `cron` | `interval` | `once`
- `id` (optional) — job id; omit to auto-generate `job_` + short guid
- `name` (optional) — display name
- `agent` (optional) — agent id; only used when `taskType=agent`

## Schedule examples

- cron: `{"type":"cron","cron":"0 9 * * *","timezone":"Asia/Shanghai"}`
- interval: `{"type":"interval","every":"6h"}`
- once: `{"type":"once","runAt":"2026-07-20T10:00:00"}`

## Examples

Text reminder:

```json
{
  "taskType": "text",
  "name": "sleep",
  "prompt": "该睡觉了，早点休息。",
  "schedule": { "type": "cron", "cron": "0 23 * * *", "timezone": "Asia/Shanghai" }
}
```

Agent job:

```json
{
  "taskType": "agent",
  "prompt": "帮我查询未读邮件.",
  "schedule": { "type": "interval", "every": "6h" }
}
```

## Session binding

- `SessionId` is taken from the current session (`ToolContext.SessionId`).
- Outbound Channel/UserId are resolved from that Session at run time — **not** stored on the Job.
- Do not invent Channel/UserId arguments; they are not accepted by `cron_create`.
