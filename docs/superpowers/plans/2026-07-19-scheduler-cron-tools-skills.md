# Scheduler Cron Tools + Session Outbound Binding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Agent 通过 cron Tools/Skills 管理定时任务，并把出站 Channel/UserId 落到 Session 正式字段（Job 只绑 SessionId）。

**Architecture:** `AddSeeingScheduler` 自注册 6 个 `ITool` + 4 个嵌入 Skill；`SessionData.ChannelId/UserId` 由 Gateway 空值回填或 WebUI 编辑；`GatewayScheduleDispatcher` 投递时从 Session 反查出站目标；Job 模型删除 Channel/UserId，无兼容 fallback。

**Tech Stack:** .NET 10, xUnit, Moq, FluentAssertions, Quartz, Blazor WebUI

**Spec:** `docs/superpowers/specs/2026-07-19-scheduler-cron-tools-skills-design.md`

## Global Constraints

- 不 git commit（除非用户另行要求）；跳过所有 Commit 步骤
- Job 侧不保留 Channel/UserId 兼容逻辑；旧 JSON 字段加载时忽略并在保存时消失
- Tools 不感知 Channel；`cron_create` 仅写当前 `ToolContext.SessionId`
- 创建仅 `agent` 型；调度 `cron` / `interval` / `once`
- 斜杠命令 `/cron-list` 等保持现状不扩 CRUD
- Gateway 入站仅当 Session 字段为空时回填；WebUI 可覆盖

---

## File Structure

| 文件 | 职责 |
|------|------|
| `src/Seeing.Session/Core/SessionData.cs` | 新增 `ChannelId` / `UserId`；`Clone()` 复制 |
| `src/Seeing.Agent.App/Execution/ExecutionJobService.cs` | Submit 时空值回填并 Save |
| `src/Seeing.Agent.Scheduler/Models/SchedulerModels.cs` | `DispatchTarget` 仅保留 `SessionId` |
| `src/Seeing.Agent.Scheduler/Jobs/JobDataKeys.cs` | 删除 `DispatchChannel` / `DispatchUserId` |
| `src/Seeing.Agent.Scheduler/Management/ScheduleManager.cs` | 注册 Job 时不再写 channel keys |
| `src/Seeing.Agent.Scheduler/Jobs/AgentJob.cs` | 不再从 JobData 读 channel；Dispatch 只传 SessionId |
| `src/Seeing.Agent.Gateway/Scheduling/GatewayScheduleDispatcher.cs` | 从 Session 解析 Channel/UserId 再出站 |
| `src/Seeing.Agent.Scheduler/Tools/*.cs` | 六个 cron Tool |
| `src/Seeing.Agent.Scheduler/Skills/*.md` | 四个嵌入 Skill Markdown |
| `src/Seeing.Agent.Scheduler/Skills/SchedulerSkillRegistrationHostedService.cs` | 启动注册 Skill |
| `src/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs` | DI 注册 Tools + Skill HostedService |
| `samples/.../Pages/SessionsPage.razor` | 列表展示 + 编辑 Channel/UserId |
| `samples/.../Pages/Session.razor` | 会话页展示 + 编辑 |
| `samples/.../Components/CronJobs/JobEditorModal.razor` | 移除 Channel/UserId 表单项 |
| `tests/Seeing.Agent.Scheduler.Tests/...` | Tools / 迁移 / 投递反查测试 |
| `tests/Seeing.Session.Tests/...`（或新建） | Session 字段序列化测试 |

---

### Task 1: SessionData 出站字段

**Files:**
- Modify: `src/Seeing.Session/Core/SessionData.cs`
- Test: `tests/Seeing.Session.Tests/SessionDataTests.cs`（追加用例）

**Interfaces:**
- Produces: `SessionData.ChannelId` (`string?`), `SessionData.UserId` (`string?`)；`Clone()` 包含二者

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Clone_CopiesChannelIdAndUserId()
{
    var s = SessionData.Create();
    s.ChannelId = "qq";
    s.UserId = "u1";
    var c = s.Clone();
    c.ChannelId.Should().Be("qq");
    c.UserId.Should().Be("u1");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Seeing.Session.Tests --filter Clone_CopiesChannelIdAndUserId`  
（若测试项目路径不同，先 `dotnet sln` / Glob 确认；Expected: 编译失败或断言失败）

- [ ] **Step 3: Add fields + Clone**

在 `SessionData` 身份/状态区域附近增加：

```csharp
/// <summary>出站渠道 ID（如 qq / wecom）；空表示仅本地 Session</summary>
public string? ChannelId { get; set; }

/// <summary>出站用户/对端 ID；可空</summary>
public string? UserId { get; set; }
```

在 `Clone()` 中复制 `ChannelId = ChannelId, UserId = UserId`。

- [ ] **Step 4: Run test to verify it passes**

Run: 同 Step 2；Expected: PASS

---

### Task 2: 入站空值回填 Channel/UserId

**Files:**
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`（`SubmitAsync` 中 EnsureSession 并保存用户消息处）
- Test: `tests/Seeing.Agent.Tests/App/ExecutionJobServiceOutboundBackfillTests.cs`（可用轻量 mock；若集成成本高，抽静态 helper 测）

**Interfaces:**
- Consumes: `SessionData.ChannelId` / `UserId`；`ChatOptions.ChannelId` / `UserId`
- Produces: `TryBackfillSessionOutbound(SessionData session, ChatOptions? options) -> bool`（changed）

- [ ] **Step 1: 抽出纯函数并写测试**

```csharp
public static bool TryBackfillSessionOutbound(SessionData session, string? channelId, string? userId)
{
    var changed = false;
    if (string.IsNullOrWhiteSpace(session.ChannelId) && !string.IsNullOrWhiteSpace(channelId))
    {
        session.ChannelId = channelId.Trim();
        changed = true;
    }
    if (string.IsNullOrWhiteSpace(session.UserId) && !string.IsNullOrWhiteSpace(userId))
    {
        session.UserId = userId.Trim();
        changed = true;
    }
    return changed;
}
```

测试：空字段被填；已有字段不被覆盖。

- [ ] **Step 2: 在 `SubmitAsync` 保存用户消息前调用**

```csharp
var session = await sessionManager.EnsureSessionAsync(sessionId);
if (TryBackfillSessionOutbound(session, options?.ChannelId, options?.UserId))
{
    // 与用户消息一起 Save
}
session.Messages.Add(userMessage);
await sessionManager.SaveAsync(sessionId);
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Seeing.Agent.Tests --filter Backfill`  
Expected: PASS

---

### Task 3: 清理 Job DispatchTarget / JobDataKeys（无兼容）

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Models/SchedulerModels.cs` — `DispatchTarget` 只留 `SessionId`
- Modify: `src/Seeing.Agent.Scheduler/Jobs/JobDataKeys.cs` — 删除 `DispatchChannel`、`DispatchUserId`
- Modify: `src/Seeing.Agent.Scheduler/Management/ScheduleManager.cs` — `RegisterUserJobAsync` 不再写 channel/userId keys
- Modify: `src/Seeing.Agent.Scheduler/Jobs/AgentJob.cs` — 删除所有 `DispatchChannel`/`DispatchUserId` 读写；`DispatchAsync` 只传 `SessionId`
- Modify: `src/Seeing.Agent.Scheduler/Models/SchedulerModels.cs` — `JobExecutionRecord` 若含 `DispatchChannel`/`DispatchUserId` 同步删除或停止赋值
- Test: `tests/Seeing.Agent.Scheduler.Tests/DispatchTargetCleanupTests.cs`

**Interfaces:**
- Produces: `DispatchTarget { string? SessionId }`；JobData 无 channel keys

- [ ] **Step 1: Write failing test — 反序列化旧 JSON 后 Channel 不存在**

```csharp
[Fact]
public void Deserialize_OldJobsJson_IgnoresChannelAndUserId()
{
    var json = """
    {"version":2,"jobs":[{"id":"j1","dispatch":{"target":{"sessionId":"main","channel":"qq","userId":"u1"}}}]}
    """;
    var file = JsonSerializer.Deserialize<JobsFile>(json, /* 与仓库相同的选项 */);
    file!.Jobs[0].Dispatch.Target.SessionId.Should().Be("main");
    // DispatchTarget 无 Channel 属性 → 编译期保证；序列化往返不应再写出 channel
    var roundtrip = JsonSerializer.Serialize(file);
    roundtrip.Should().NotContain("channel");
    roundtrip.Should().NotContain("userId");
}
```

- [ ] **Step 2: 改模型与引用，修复编译错误**

`DispatchTarget`:

```csharp
public class DispatchTarget
{
    public string? SessionId { get; set; }
}
```

全库删除对 `DispatchTarget.Channel` / `.UserId` 与 `JobDataKeys.DispatchChannel` / `DispatchUserId` 的引用。

`AgentJob.DispatchAsync` 改为：

```csharp
var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId) ?? defaultSessionId;
await _dispatcher.DispatchAsync(new DispatchRequest
{
    Source = ScheduleSources.Cron,
    TaskType = ScheduleTaskTypes.Agent,
    Content = content,
    UserInput = userInput,
    SessionId = dispatchSessionId
    // 不设 Channel / UserId
}, ct);
```

- [ ] **Step 3: Run Scheduler tests**

Run: `dotnet test tests/Seeing.Agent.Scheduler.Tests`  
Expected: PASS（修复因删除属性导致的测试编译错误）

---

### Task 4: Dispatcher 从 Session 反查出站

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Scheduling/GatewayScheduleDispatcher.cs`
- Test: `tests/Seeing.Agent.Tests/...` 或 Gateway 相关测试项目中 mock `ISessionManager`

**Interfaces:**
- Consumes: `request.SessionId` → `ISessionManager.Get` / `EnsureSessionAsync` → `session.ChannelId` / `UserId`
- Produces: `PushChannelOutbound` 仅在 `ChannelId` 非空时调用

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task Dispatch_UsesSessionChannelId_NotRequestChannel()
{
    // session.ChannelId = "qq"; request.Channel 故意为 null 或 "ignored"
    // assert PushChannelOutbound 收到 channel=qq
}

[Fact]
public async Task Dispatch_NoSessionChannel_SkipsOutbound_StillOk()
{
    // session.ChannelId = null → 不调用 PushChannelOutbound；Success=true
}
```

- [ ] **Step 2: Implement resolution in GatewayScheduleDispatcher**

在写 Session 之后、出站之前：

```csharp
var session = _sessionManager.Get(request.SessionId) 
    ?? await _sessionManager.EnsureSessionAsync(request.SessionId);
var channel = session.ChannelId;
var userId = session.UserId;

if (!string.IsNullOrWhiteSpace(channel) && _connectionManager != null)
{
    var delivered = _connectionManager.PushChannelOutbound(new GatewayChannelOutboundPayload
    {
        Channel = channel.Trim(),
        SessionId = request.SessionId,
        Text = request.Content,
        Source = $"scheduler.{request.Source}",
        UserId = userId
    });
    // 现有 Warning 逻辑保留
}
else
{
    _logger.LogDebug("No ChannelId on session {SessionId}; skip channel outbound", request.SessionId);
}
```

删除对 `request.Channel` / `request.UserId` 作为出站源的依赖（`DispatchRequest` 可暂时保留属性供其他调用方，但本 Dispatcher 忽略它们；若全库无写入则可后续删属性——本 Task 至少保证出站源唯一为 Session）。

- [ ] **Step 3: Run tests**

Expected: PASS

---

### Task 5: cron_list / cron_run / cron_pause / cron_resume / cron_delete Tools

**Files:**
- Create: `src/Seeing.Agent.Scheduler/Tools/CronListTool.cs`
- Create: `src/Seeing.Agent.Scheduler/Tools/CronRunTool.cs`
- Create: `src/Seeing.Agent.Scheduler/Tools/CronPauseTool.cs`
- Create: `src/Seeing.Agent.Scheduler/Tools/CronResumeTool.cs`
- Create: `src/Seeing.Agent.Scheduler/Tools/CronDeleteTool.cs`
- Test: `tests/Seeing.Agent.Scheduler.Tests/Tools/CronControlToolsTests.cs`

**Interfaces:**
- Consumes: `IScheduleManager`
- Produces: Tool ids `cron_list`, `cron_run`, `cron_pause`, `cron_resume`, `cron_delete`

模式对齐 `MemorySearchTool`：继承 `ToolBase`，构造注入 `ILogger` + `IScheduleManager`。

- [ ] **Step 1: Write failing tests with Moq `IScheduleManager`**

覆盖：list 空/有数据；run Accepted/NotFound/Disabled；pause/resume/delete 成功与 NotFound。

- [ ] **Step 2: Implement five tools**

`cron_list` 输出示例行：`- {id} intent={Intent} next={NextFireTime} agent={Agent}`（复用 `GetJobStatusAsync`）。

`cron_run`：参数 `id` → `RunJobOnceAsync` → 映射 `TriggerResult`。

`cron_pause` / `cron_resume` / `cron_delete`：参数 `id`；delete 用 `DeleteJobAsync` 返回 bool。

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Seeing.Agent.Scheduler.Tests --filter CronControl`  
Expected: PASS

---

### Task 6: cron_create Tool

**Files:**
- Create: `src/Seeing.Agent.Scheduler/Tools/CronCreateTool.cs`
- Create: `src/Seeing.Agent.Scheduler/Tools/CronScheduleParser.cs`（可选：把 JsonElement → `ScheduleSpec` 抽出来便于测）
- Test: `tests/Seeing.Agent.Scheduler.Tests/Tools/CronCreateToolTests.cs`

**Interfaces:**
- Consumes: `IScheduleManager.CreateOrReplaceJobAsync`；`ScheduleExpressionParser`
- Produces: Tool id `cron_create`；写入的 `ScheduledJobSpec` 仅含 `Dispatch.Target.SessionId = context.SessionId`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Create_BindsCurrentSession_Only()
{
    // Execute with ToolContext.SessionId = "ses_1"
    // capture CreateOrReplaceJobAsync arg
    // assert TaskType=agent, Prompt set, Dispatch.Target.SessionId == "ses_1"
    // assert no Channel property on target
}

[Fact]
public async Task Create_InvalidCron_ReturnsFailure()
{
    // schedule.type=cron, cron="not-a-cron" → Success=false, message contains example
}
```

校验：调用 `ScheduleExpressionParser.GetNextOccurrence` 或 `NormalizeCron`；非法则 Failure。

参数 schema（要点）：

```csharp
// id?, name?, prompt (required), agent?, schedule: { type, cron?, every?, runAt?, timezone? }
```

- [ ] **Step 2: Implement CronCreateTool**

```csharp
var job = new ScheduledJobSpec
{
    Id = string.IsNullOrWhiteSpace(id) ? $"job_{Guid.NewGuid():N}"[..16] : id.Trim(),
    Name = name,
    TaskType = ScheduleTaskTypes.Agent,
    Prompt = prompt,
    Agent = agent,
    Intent = ScheduleIntent.Active,
    Schedule = parsedSchedule,
    Dispatch = new DispatchSpec
    {
        Target = new DispatchTarget { SessionId = context.SessionId }
    }
};
await _manager.CreateOrReplaceJobAsync(job, context.CancellationToken);
```

- [ ] **Step 3: Run tests**

Expected: PASS

---

### Task 7: DI 注册六个 Tools

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs`（两个 `AddSeeingScheduler` 重载都要注册）
- Test: `tests/Seeing.Agent.Scheduler.Tests/SchedulerToolRegistrationTests.cs`

**Interfaces:**
- Produces: `services.AddSingleton<ITool>(sp => sp.GetRequiredService<CronXxxTool>())` × 6  
  （与 Memory 相同：**禁止** `TryAddSingleton<ITool>`）

- [ ] **Step 1: Write registration test**

```csharp
[Fact]
public void AddSeeingScheduler_RegistersSixCronTools()
{
    var services = new ServiceCollection();
    // 注册最小依赖：logging、IScheduleManager mock、配置等（参照现有 Scheduler 测试 fixture）
    services.AddSeeingScheduler();
    var sp = services.BuildServiceProvider();
    var tools = sp.GetServices<ITool>().Select(t => t.Id).ToHashSet();
    tools.Should().Contain(new[] { "cron_list", "cron_create", "cron_delete", "cron_pause", "cron_resume", "cron_run" });
}
```

注意：若 `AddSeeingScheduler` 强依赖 Quartz/选项，用现有 `SchedulerTestWorkspace` 或精简注册路径；必要时把 Tool 注册抽成 `AddSeeingSchedulerTools(services)` 以便测。

- [ ] **Step 2: Wire DI**

```csharp
services.AddSingleton<CronListTool>();
// ... 其余 5 个
services.AddSingleton<ITool>(sp => sp.GetRequiredService<CronListTool>());
// ...
```

确认宿主 `ToolInvoker` 仍通过 `GetServices<ITool>()` 自动注册（`AddSeeingAgent` 已有逻辑）。**顺序：** 宿主需先 `AddSeeingAgent` 再 `AddSeeingScheduler`，或确保 Scheduler Tools 在 ToolInvoker 构建前已入 DI（对照 WebUI `Program.cs`）。

- [ ] **Step 3: Run tests + build WebUI**

Run: `dotnet test tests/Seeing.Agent.Scheduler.Tests --filter Registration`  
`dotnet build samples/Seeing.Agent.WebUI`  
Expected: PASS / 成功

---

### Task 8: 嵌入 Skills + 启动注册

**Files:**
- Create: `src/Seeing.Agent.Scheduler/Skills/cron-management/SKILL.md`
- Create: `src/Seeing.Agent.Scheduler/Skills/cron-create/SKILL.md`
- Create: `src/Seeing.Agent.Scheduler/Skills/cron-list-run/SKILL.md`
- Create: `src/Seeing.Agent.Scheduler/Skills/cron-lifecycle/SKILL.md`
- Modify: `src/Seeing.Agent.Scheduler/Seeing.Agent.Scheduler.csproj` — EmbeddedResource
- Create: `src/Seeing.Agent.Scheduler/Skills/SchedulerSkillRegistrationHostedService.cs`
- Modify: `SchedulerServiceCollectionExtensions.cs` — `AddHostedService<SchedulerSkillRegistrationHostedService>()`
- Test: `tests/Seeing.Agent.Scheduler.Tests/SchedulerSkillRegistrationTests.cs`

**Interfaces:**
- Produces: Skill names `cron-management`, `cron-create`, `cron-list-run`, `cron-lifecycle`
- Consumes: `SkillManager.RegisterSkill(SkillInfo)`

- [ ] **Step 1: Write SKILL.md contents**

每个文件 YAML frontmatter：`name`、`description`（触发用）、正文说明对应工具与流程。

`cron-management`：总览 + Session 绑定语义（出站来自 Session，不填 channel）。  
`cron-create`：收集 prompt/schedule → `cron_create`。  
`cron-list-run`：`cron_list` / `cron_run`。  
`cron-lifecycle`：pause/resume/delete；删除前先 list。

- [ ] **Step 2: csproj EmbeddedResource**

```xml
<ItemGroup>
  <EmbeddedResource Include="Skills\**\SKILL.md" />
</ItemGroup>
```

- [ ] **Step 3: HostedService 加载并 RegisterSkill**

```csharp
public Task StartAsync(CancellationToken cancellationToken)
{
    var skillManager = _services.GetService<SkillManager>();
    if (skillManager is null)
    {
        _logger.LogWarning("SkillManager not registered; skip scheduler skills");
        return Task.CompletedTask;
    }
    foreach (var skill in LoadEmbeddedSkills())
        skillManager.RegisterSkill(skill);
    return Task.CompletedTask;
}
```

`Location` = `embedded://scheduler/{name}`；`Content` = Markdown body（无 frontmatter）。

- [ ] **Step 4: Test registration with SkillManager + HostedService.StartAsync**

Expected: 4 names present via `SkillManager` 查询 API（确认现有 `GetSkill` / 列表方法名后使用）。

---

### Task 9: WebUI — Job 编辑页去掉 Channel/UserId

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/CronJobs/JobEditorModal.razor`

- [ ] **Step 1: 删除投递配置中 Channel、UserId 的 FormItem；Session 表单项保留并说明「出站渠道在会话上配置」**
- [ ] **Step 2: 删除相关验证/初始化中对 `Dispatch.Target.Channel` / `UserId` 的引用**
- [ ] **Step 3: `dotnet build samples/Seeing.Agent.WebUI`** — Expected: 成功

---

### Task 10: WebUI — 会话列表与会话页展示/编辑出站字段

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/SessionsPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`
- Modify: 若需 Save API — 确认 `IChatOrchestrator` / `ISessionManager.SaveAsync` 已够用

- [ ] **Step 1: SessionsPage 卡片 meta 增加 Channel / UserId 展示（空显示「未绑定」）**
- [ ] **Step 2: 增加「编辑出站」按钮 + Modal：两个 Input，确认后写 `session.ChannelId`/`UserId` 并 `SaveAsync`**
- [ ] **Step 3: Session.razor 在 Agent/Model 设置附近增加 ChannelId、UserId 输入；变更时写回 `CurrentSession` 并 Save**
- [ ] **Step 4: 手工清单（写入 PR/计划备注即可）**
  - 列表可见绑定
  - 编辑保存后刷新仍在
  - Gateway 入站不会覆盖已设置的 Channel

---

### Task 11: 集成冒烟与回归

**Files:**
- Test: `tests/Seeing.Agent.Scheduler.Tests/CronToolsIntegrationTests.cs`（可选轻量）

- [ ] **Step 1: 端到端（内存）** — Create via tool → List 可见 → Pause → Run 失败或按 Intent 行为 → Resume → Delete
- [ ] **Step 2: `dotnet test tests/Seeing.Agent.Scheduler.Tests`**
- [ ] **Step 3: `dotnet test tests/Seeing.Session.Tests`（若有）+ `dotnet build Seeing.Agent.slnx`**
- [ ] **Step 4: 确认无残留 `DispatchTarget.Channel` / `JobDataKeys.DispatchChannel` 引用**

Run: `rg "DispatchChannel|DispatchTarget\.Channel|target\.channel" src tests samples --glob '!**/docs/**'`  
Expected: 无业务代码命中（文档中旧设计可保留）

---

## Self-Review (plan vs spec)

| Spec 要求 | Task |
|-----------|------|
| 6 cron Tools | 5–7 |
| 4 Skills + 嵌入注册 | 8 |
| Session ChannelId/UserId 正式字段 | 1 |
| Gateway 空值回填 | 2 |
| Job 去掉 Channel/UserId、无兼容 | 3 |
| 投递从 Session 反查 | 4 |
| WebUI 会话展示编辑 | 10 |
| Job 编辑页移除 Channel/UserId | 9 |
| 斜杠命令不动 | （无 Task，约束） |
| 测试覆盖 | 各 Task + 11 |

无 TBD 占位；字段名统一 `ChannelId`/`UserId`。
