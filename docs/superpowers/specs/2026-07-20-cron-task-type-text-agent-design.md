# cron_create 支持 text / agent 任务类型 — 设计方案

**日期:** 2026-07-20  
**状态:** 待实现  
**范围:** `Seeing.Agent.Scheduler` 的 `CronCreateTool`、嵌入 Skills（`cron-create` / `cron-management`）、相关单测  
**相关既有设计:** `2026-07-19-scheduler-cron-tools-skills-design.md`（当时非目标含「text 型任务创建」；本方案补齐）

---

## 1. 背景与目标

### 问题

- 调度执行层已支持 `ScheduleTaskTypes.Text` / `Agent`（`AgentJob` 分路执行）。
- `cron_create` 将 `TaskType` **写死为 agent**，导致「提醒睡觉」等纯通知任务也被当成 agent 任务，浪费推理与工具调用。
- Skills 只描述 agent 任务，未指导何时选 text。

### 目标

1. Agent 创建定时任务时可显式选择 `taskType`：`text` | `agent`。
2. `taskType` **必填**，不传即失败，强制做选型。
3. 对外内容字段统一为 `prompt`；工具层按类型映射到内部 `Text` / `Prompt`。
4. 优化 SKILL，写清何时选 text、何时选 agent。

### 非目标

- CLI / WebUI 创建入口改造
- 改动 `ScheduledJobSpec` / `AgentJob` / 投递协议（已支持两种类型）
- 自动迁移已误建为 agent 的提醒类历史任务

---

## 2. 决策摘要

| 项 | 选择 |
|----|------|
| 能力形态 | 扩展现有 `cron_create`（不拆双工具） |
| `taskType` | **必填**：`text` \| `agent`；非法值报错 |
| 内容字段 | 统一 `prompt`；text→`Text`，agent→`Prompt` |
| `agent` 参数 | 仅 agent 任务有意义；text 时忽略 |
| Session 绑定 | 不变：`ToolContext.SessionId` |
| Skills | 更新 `cron-create`（详）+ `cron-management`（概览一句） |

---

## 3. 工具 API

### `cron_create` 参数

| 参数 | 必填 | 说明 |
|------|------|------|
| `taskType` | 是 | `text` \| `agent` |
| `prompt` | 是 | text：最终投递给用户的文案；agent：给 Agent 的任务指令 |
| `schedule` | 是 | `cron` / `interval` / `once`（不变） |
| `id` | 否 | 省略则 `job_` + short guid |
| `name` | 否 | 显示名 |
| `agent` | 否 | 仅 `taskType=agent` 时有意义 |

### 内部映射

```
taskType=text  → TaskType=text,  Text=prompt, Prompt=null
taskType=agent → TaskType=agent, Prompt=prompt, Text=null
```

### 校验

- 缺 `taskType` 或非法值 → Failure，提示必须为 `text` 或 `agent`
- 缺 `prompt` / `schedule` → 保持现有失败行为
- Tool Description / ParametersSchema 同步反映上述契约

---

## 4. SKILL 选型规则

### 选 `text` 当

- 纯提醒 / 通知（如「该睡觉了」「开会前 10 分钟提醒」）
- 到期只需投递固定文案，不需要推理、查资料、调工具
- 内容在创建时已定稿，触发时原样发送

### 选 `agent` 当

- 触发时需要再跑 Agent（总结、检查、生成报告、执行工具链）
- 内容依赖当时上下文，不能只发死文案
- 用户明确要求「到点让 agent 去做某事」

### 默认倾向

- 拿不准且只是「到点告诉用户一句话」→ `text`
- 只有确实要「到点让 agent 工作」→ `agent`

### 文案建议

- `text` 的 `prompt`：用户最终看到的提醒正文
- `agent` 的 `prompt`：给 agent 的任务指令，不是直接给人看的话术

### 文档落点

- `cron-create/SKILL.md`：参数表 + 选型规则 + 示例
- `cron-management/SKILL.md`：`cron_create` 一行改为「创建 text/agent 任务」，并指向选型细节

---

## 5. 测试

- 缺 `taskType` → 失败
- 非法 `taskType` → 失败
- `taskType=text` → `TaskType=text`，`Text=prompt`，`Prompt` 为空
- `taskType=agent` → 现有成功路径补传 `taskType`，断言不变
- 可选：text 任务忽略传入的 `agent` 参数

---

## 6. 成功标准

- Agent 创建提醒类任务可选 `text` 并投递固定文案
- 真正需要执行的任务仍走 `agent`
- SKILL 足以指导选型，减少「提醒却跑 agent」
