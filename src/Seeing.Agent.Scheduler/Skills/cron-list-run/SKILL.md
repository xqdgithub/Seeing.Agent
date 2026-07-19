---
name: cron-list-run
description: List scheduled jobs with cron_list and trigger one immediately with cron_run.
---

# Cron List & Run

## `cron_list`

- Lists all scheduled jobs.
- No parameters.
- Output includes job id, intent, and next fire time when available.

## `cron_run`

- Immediately runs a job once without waiting for its schedule.
- Parameter: `id` (required) — the job id from `cron_list`.

Typical flow: call `cron_list` to find an id, then `cron_run` with that id.
