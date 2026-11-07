---
name: heartbeat-management
description: Read and adjust the periodic heartbeat (interval agent run) — separate from cron jobs.
---

# Heartbeat

心跳是一个独立的周期任务。它的配置与执行历史都不在定时任务里。

| Tool | Purpose |
|------|---------|
| `heartbeat_get` | 读配置、是否启用、下次触发、上次执行 |
| `heartbeat_update` | 局部更新（未传字段不变） |
| `heartbeat_run` | 立即执行一次 |

## Boundaries

- 心跳不出现在 `cron_list`，也不能被 `cron_run` 触发；反之 cron 任务不出现在 `heartbeat_get`。
- 心跳只支持间隔（`every`），不接受 cron 表达式或一次性时间。
- `windows` 语义与定时任务的生效时段一致：空表示全天，允许跨午夜，禁止交叠。
- 心跳结果只写一条已消费的助手消息，不会在目标会话留下未读指令。

## When to use

用户说「定期检查一下」「每隔几小时看一眼」但**不需要固定钟点**时用心跳；
需要固定钟点（每天 9 点）时用 `cron_create`。

## Common settings

- 间隔：`6h`、`30m`。数字 + 单位（s/m/h/d）。
- 目标：`session_id`（写回该会话）或 `conversation`（出站推送），至少一项。
- 生效时段：只在指定墙钟区间内触发，例如 `{"start":"09:00","end":"18:00"}`。
