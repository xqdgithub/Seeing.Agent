---
name: cron-create
description: Create or replace agent scheduled jobs with cron_create (SessionId-bound; schedule cron/interval/once).
---

# Cron Create

Use tool `cron_create` to create or replace an **agent** scheduled job.

## Parameters

- `prompt` (required) — prompt the agent runs when the job fires
- `schedule` (required) — object with `type` = `cron` | `interval` | `once`
- `id` (optional) — job id; omit to auto-generate `job_` + short guid
- `name` (optional) — display name
- `agent` (optional) — agent id

## Schedule examples

- cron: `{"type":"cron","cron":"0 9 * * *","timezone":"Asia/Shanghai"}`
- interval: `{"type":"interval","every":"6h"}`
- once: `{"type":"once","runAt":"2026-07-20T10:00:00"}`

## Session binding

- `SessionId` is taken from the current session (`ToolContext.SessionId`).
- Outbound Channel/UserId are resolved from that Session at run time — **not** stored on the Job.
- Do not invent Channel/UserId arguments; they are not accepted by `cron_create`.
