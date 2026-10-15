# cron_create text/agent TaskType Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `cron_create` 支持必填 `taskType`（`text`|`agent`），统一用 `prompt` 传内容，并更新 Skills 指导选型，避免提醒类任务误建成 agent。

**Architecture:** 仅扩展 `CronCreateTool`：校验 `taskType`，按类型把 `prompt` 映射到 `ScheduledJobSpec.Text` 或 `Prompt`；底层 `AgentJob` / `ScheduleManager` 已支持两种类型，不改执行路径。同步更新嵌入 Skills 与单测。

**Tech Stack:** .NET 10, xUnit, Moq, FluentAssertions

**Spec:** `docs/superpowers/specs/2026-07-20-cron-task-type-text-agent-design.md`

## Global Constraints

- 不 git commit（除非用户另行要求）；跳过所有 Commit 步骤
- `taskType` **必填**；缺省或非法值必须 Failure
- 对外内容字段统一为 `prompt`；text→`Text`，agent→`Prompt`
- 不改 CLI / WebUI；不迁移历史误建任务
- 不改 `ScheduledJobSpec` / `AgentJob` / 投递协议

---

## File Structure

| 文件 | 职责 |
|------|------|
| `src/Seeing.Agent.Scheduler/Tools/CronCreateTool.cs` | 新增 `taskType` 校验与 Text/Prompt 映射；更新 Description/Schema |
| `src/Seeing.Agent.Scheduler/Skills/cron-create/SKILL.md` | 参数 + 选型规则 + 示例 |
| `src/Seeing.Agent.Scheduler/Skills/cron-management/SKILL.md` | 概览改为支持 text/agent |
| `tests/Seeing.Agent.Scheduler.Tests/Tools/CronCreateToolTests.cs` | 缺/非法 taskType、text 映射、现有用例补 taskType |

---

### Task 1: CronCreateTool 必填 taskType + 映射

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Tools/CronCreateTool.cs`
- Modify: `tests/Seeing.Agent.Scheduler.Tests/Tools/CronCreateToolTests.cs`

**Interfaces:**
- Consumes: `ScheduleTaskTypes.Text` / `ScheduleTaskTypes.Agent`；`IScheduleManager.CreateOrReplaceJobAsync`
- Produces: `cron_create` 接受必填 `taskType`；`text` 时 `job.Text=prompt`；`agent` 时 `job.Prompt=prompt`

- [ ] **Step 1: 更新现有成功用例并写失败/text 用例（先红）**

在 `CronCreateToolTests.cs` 中：

1. 给 `ValidCron_ShouldCreateAgentJobWithSessionIdFromContext`、`MissingId_ShouldGenerateJobPrefixedId`、`OnceSchedule_ShouldAcceptRunAt` 的 Args **补上** `taskType = "agent"`。
2. 新增以下测试：

```csharp
[Fact]
public async Task MissingTaskType_ShouldFail()
{
    var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, Mock.Of<IScheduleManager>());
    var result = await tool.ExecuteAsync(
        Args(new
        {
            prompt = "remind sleep",
            schedule = new { type = "cron", cron = "0 23 * * *" }
        }),
        new ToolContext { SessionId = "sess-1" });

    result.Success.Should().BeFalse();
    result.Error.Should().MatchRegex("(?i)taskType|text|agent");
}

[Fact]
public async Task InvalidTaskType_ShouldFail()
{
    var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, Mock.Of<IScheduleManager>());
    var result = await tool.ExecuteAsync(
        Args(new
        {
            taskType = "reminder",
            prompt = "x",
            schedule = new { type = "once", runAt = "2026-12-01T10:00:00" }
        }),
        new ToolContext { SessionId = "sess-1" });

    result.Success.Should().BeFalse();
    result.Error.Should().MatchRegex("(?i)taskType|text|agent");
}

[Fact]
public async Task TextTaskType_ShouldMapPromptToText()
{
    ScheduledJobSpec? captured = null;
    var manager = new Mock<IScheduleManager>();
    manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
        .Callback<ScheduledJobSpec, CancellationToken>((job, _) => captured = job)
        .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);

    var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object);
    var result = await tool.ExecuteAsync(
        Args(new
        {
            taskType = "text",
            name = "Sleep reminder",
            prompt = "该睡觉了",
            agent = "sisyphus", // 应被忽略
            schedule = new { type = "cron", cron = "0 23 * * *" }
        }),
        new ToolContext { SessionId = "sess-text" });

    result.Success.Should().BeTrue();
    captured.Should().NotBeNull();
    captured!.TaskType.Should().Be(ScheduleTaskTypes.Text);
    captured.Text.Should().Be("该睡觉了");
    captured.Prompt.Should().BeNull();
    captured.Agent.Should().BeNull();
    captured.Dispatch.Target.SessionId.Should().Be("sess-text");
}

[Fact]
public async Task AgentTaskType_ShouldMapPromptToPrompt()
{
    ScheduledJobSpec? captured = null;
    var manager = new Mock<IScheduleManager>();
    manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
        .Callback<ScheduledJobSpec, CancellationToken>((job, _) => captured = job)
        .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);

    var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object);
    var result = await tool.ExecuteAsync(
        Args(new
        {
            taskType = "agent",
            prompt = "summarize inbox",
            agent = "sisyphus",
            schedule = new { type = "interval", every = "6h" }
        }),
        new ToolContext { SessionId = "s1" });

    result.Success.Should().BeTrue();
    captured!.TaskType.Should().Be(ScheduleTaskTypes.Agent);
    captured.Prompt.Should().Be("summarize inbox");
    captured.Text.Should().BeNull();
    captured.Agent.Should().Be("sisyphus");
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter "FullyQualifiedName~CronCreateToolTests"
```

Expected: `MissingTaskType_ShouldFail` / `InvalidTaskType_ShouldFail` / `TextTaskType_ShouldMapPromptToText` 等失败（现实现写死 agent 且不校验 taskType）；已补 `taskType` 的旧用例若仍硬编码成功路径可能仍绿。

- [ ] **Step 3: 实现 CronCreateTool**

将 `CronCreateTool.cs` 改为以下逻辑（保持类结构，替换 Description / Schema / ExecuteAsync 核心）：

```csharp
public override string Description =>
    "创建或替换定时任务。必填：taskType（text|agent）、prompt、schedule（cron/interval/once）；可选 id/name/agent。" +
    " text=到点投递固定文案；agent=到点运行 Agent。SessionId 自动取自当前会话。";

public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new
    {
        taskType = new
        {
            type = "string",
            description = "任务类型：text（固定文案提醒）| agent（运行 Agent）。必填。"
        },
        id = new { type = "string", description = "任务 ID；省略则自动生成 job_ 前缀 ID" },
        name = new { type = "string", description = "任务显示名称" },
        prompt = new
        {
            type = "string",
            description = "text：用户看到的提醒正文；agent：给 Agent 的任务指令"
        },
        agent = new { type = "string", description = "Agent ID（仅 taskType=agent 时有效）" },
        schedule = new
        {
            type = "object",
            description = "调度配置：type=cron|interval|once，配合 cron/every/runAt/timezone",
            properties = new
            {
                type = new { type = "string", description = "cron | interval | once" },
                cron = new { type = "string", description = "Cron 表达式（type=cron）" },
                every = new { type = "string", description = "间隔，如 6h、30m（type=interval）" },
                runAt = new { type = "string", description = "一次性执行时间 ISO8601（type=once）" },
                timezone = new { type = "string", description = "时区 ID，默认本地" }
            },
            required = new[] { "type" }
        }
    },
    required = new[] { "taskType", "prompt", "schedule" }
});
```

在 `ExecuteAsync` 中，解析 `prompt` / `schedule` 之后、构造 job 之前：

```csharp
var taskTypeRaw = GetStringArgument(arguments, "taskType")?.Trim();
if (string.IsNullOrWhiteSpace(taskTypeRaw))
    return Failure("taskType 参数是必需的，取值：text（固定文案提醒）或 agent（运行 Agent）");

var taskType = taskTypeRaw.ToLowerInvariant();
if (taskType != ScheduleTaskTypes.Text && taskType != ScheduleTaskTypes.Agent)
    return Failure("无效的 taskType，取值：text（固定文案提醒）或 agent（运行 Agent）");

var isText = taskType == ScheduleTaskTypes.Text;

var job = new ScheduledJobSpec
{
    Id = id,
    Name = GetStringArgument(arguments, "name")?.Trim(),
    TaskType = taskType,
    Intent = ScheduleIntent.Active,
    Text = isText ? prompt : null,
    Prompt = isText ? null : prompt,
    Agent = isText ? null : GetStringArgument(arguments, "agent")?.Trim(),
    Schedule = schedule,
    Dispatch = new DispatchSpec
    {
        Target = new DispatchTarget
        {
            SessionId = context.SessionId
        }
    }
};
```

成功消息可带类型，例如：`已创建定时任务: {id} [{taskType}/{schedule.Type}]`。

类顶部 summary 改为：`创建或替换定时任务（text|agent；SessionId 绑定自 ToolContext）。`

- [ ] **Step 4: 跑测试确认通过**

Run:

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter "FullyQualifiedName~CronCreateToolTests"
```

Expected: 全部 PASS

- [ ] **Step 5: Commit** — 跳过（见 Global Constraints）

---

### Task 2: 更新嵌入 Skills

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Skills/cron-create/SKILL.md`
- Modify: `src/Seeing.Agent.Scheduler/Skills/cron-management/SKILL.md`

**Interfaces:**
- Consumes: Task 1 的 `cron_create` 契约（`taskType` 必填，`prompt` 统一）
- Produces: Agent 可读的选型规则

- [ ] **Step 1: 重写 `cron-create/SKILL.md`**

```markdown
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
  "prompt": "Summarize unread inbox and list action items.",
  "schedule": { "type": "interval", "every": "6h" }
}
```

## Session binding

- `SessionId` is taken from the current session (`ToolContext.SessionId`).
- Outbound Channel/UserId are resolved from that Session at run time — **not** stored on the Job.
- Do not invent Channel/UserId arguments; they are not accepted by `cron_create`.
```

- [ ] **Step 2: 更新 `cron-management/SKILL.md`**

将概述与表格中 `cron_create` 行改为同时覆盖 text/agent，例如：

- 开头：`Use these tools to manage scheduled jobs (text reminders or agent runs):`
- 表格：`| cron_create | Create or replace a job (required taskType: text\|agent) |`
- 在 Session-bound 段落后加一句：`Prefer text for pure reminders; agent only when work must run at fire time. See skill cron-create.`

- [ ] **Step 3: 确认 Skill 仍能被嵌入注册（冒烟）**

Run:

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter "FullyQualifiedName~SchedulerSkillRegistrationTests"
```

Expected: PASS（嵌入资源名未改，仅内容变更）

- [ ] **Step 4: Commit** — 跳过

---

### Task 3: 回归 Scheduler Tools 测试

**Files:**
- 无新文件；验证既有 Tools 套件

- [ ] **Step 1: 跑 Scheduler.Tests 相关套件**

Run:

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter "FullyQualifiedName~Tools|FullyQualifiedName~SchedulerSkillRegistration"
```

Expected: 全部 PASS

- [ ] **Step 2: Commit** — 跳过

---

## Spec Coverage Checklist

| Spec 要求 | Task |
|-----------|------|
| `taskType` 必填 text\|agent | Task 1 |
| `prompt` 统一；text→Text，agent→Prompt | Task 1 |
| text 忽略 `agent` | Task 1（`TextTaskType_ShouldMapPromptToText`） |
| 更新 cron-create / cron-management SKILL 选型 | Task 2 |
| 单测覆盖缺/非法/两种映射 | Task 1 |
| 不改 CLI/WebUI/底层执行 | 全局约束（无 Task） |
