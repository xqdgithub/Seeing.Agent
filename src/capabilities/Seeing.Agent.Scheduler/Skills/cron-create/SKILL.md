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
- `schedule` (required) — object with `type` = `cron` | `interval` | `once`（也接受字符串简写，见下）
- `id` (optional) — job id; omit to auto-generate `job_` + short guid
- `name` (optional) — display name
- `agent` (optional) — agent id; only used when `taskType=agent`
- `conversation` (optional, **默认不填**) — 出站目标 `{channelId, kind, externalId, title?}`；省略即跟随会话绑定

创建成功后会回显任务详情（名称、类型、状态、调度规则、下次触发时间、提醒内容与目标会话），
无需再额外调用 `cron_list` 确认。

## Schedule examples

- cron: `{"type":"cron","cron":"0 9 * * *","timezone":"Asia/Shanghai"}`
- interval: `{"type":"interval","every":"6h"}`
- interval + windows: `{"type":"interval","every":"40m","timezone":"Asia/Shanghai","windows":[{"start":"09:00","end":"23:00"}]}`
- once: `{"type":"once","runAt":"2026-07-20T10:00:00"}`

`schedule` 也接受等价简写（模型可任选）：

- 裸 cron 字符串：`"0 12 * * *"`
- 间隔字符串：`"6h"`、`"30m"`
- ISO/常规时间：`"2026-10-31T12:00:00"`、`"2026-10-31 12:00:00"`
- 字符串化 JSON：`"{\"type\":\"cron\",\"cron\":\"0 12 * * *\"}"`

字段名容忍 `snake_case` 与常见别名（`run_at`、`expression`、`interval`、`tz`、`windows` 项可写成 `"09:00-23:00"`）。
若漏传 `schedule` 对象，也可把 `cron` / `every` / `runAt` / `timezone` 直接放在参数顶层。

`timezone` 中的墙钟时间语义：`runAt` 不带偏移时按 `timezone` 墙钟解释；带 `+08:00` / `Z` 等显式偏移时换算到 `timezone` 墙钟。

`windows`（仅 `interval`）：
- 可选；空/省略 = 全天
- `start` 缺省 `00:00`，`end` 缺省 `23:59:59`
- 间隔从每个时段的起点对齐；允许跨午夜；禁止交叠（端点相触允许）
- 所有时间均为 `timezone` 墙钟本地时间（不要用 UTC）

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

Interval within hours:

```json
{
  "taskType": "text",
  "name": "daytime-ping",
  "prompt": "白天巡检提醒",
  "schedule": {
    "type": "interval",
    "every": "40m",
    "timezone": "Asia/Shanghai",
    "windows": [{ "start": "09:00", "end": "23:00" }]
  }
}
```

## Outbound conversation

- `SessionId` is taken from the current session (`ToolContext.SessionId`) and cannot be overridden.
- **默认不填 `conversation`。** 省略（或留空 `{}`）即表示「跟随当前会话出站绑定」：保存任务时
  由调度器自动快照一份 `ChannelRoute`，会话归档后仍可推送。这是推荐路径。
- **不要为了补全 conversation 而额外查询/检索**（不要为此调用目录、`cron_list` 或通道工具）——
  用户没指明目标时，多查一遍既慢又容易拿到过期数据。
- 仅当用户明确说「发到 XX 群 / 给 XX 发」时，才显式填写完整三键（**优先于**会话 `ChannelRoute`）：

```json
{
  "taskType": "text",
  "prompt": "该交日报了",
  "schedule": { "type": "cron", "cron": "0 18 * * 1-5" },
  "conversation": { "channelId": "qq", "kind": "group", "externalId": "123456789", "title": "研发群" }
}
```

- `channelId` / `kind` / `externalId` 必须齐备；`kind` 取 `direct` | `group` | `guild`；`title` 可选。
  只给部分字段会报错（避免半截配置静默生效）。
- **不要凭空编造** channelId / externalId —— 用户没说清就默认不填，跟随会话绑定。
- 已存在的任务用同 `id` 再次 `cron_create` 即视为修改；`conversation` 传入完整三键即覆盖，省略则保持跟随会话。
