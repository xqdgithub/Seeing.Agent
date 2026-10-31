---
name: cron-list-run
description: List scheduled jobs with cron_list and trigger one immediately with cron_run.
---

# Cron List & Run

## `cron_list`

- Lists all scheduled jobs.
- No parameters.
- Output 每个任务包含：名称、任务类型（text/agent）、状态与下次触发时间、调度规则（cron/interval/once + 时区）、
  提醒内容或 Agent 指令、目标会话。
- 仍为启用、但已不会再次自动触发的任务会标记为「已过期」（常见于已过去的一次性提醒）。

## `cron_run`

- Immediately runs a job once without waiting for its schedule.
- Parameter: `id` (required) — the job id from `cron_list`.
- Output 回显任务名称、RunId 与调度规则。

Typical flow: call `cron_list` to find an id, then `cron_run` with that id.
