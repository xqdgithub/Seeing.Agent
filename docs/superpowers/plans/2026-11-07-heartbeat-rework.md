# 心跳重构 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把心跳从「独立的 `HeartbeatJob` + `HeartbeatOptions`」改为「调度执行内核的一个画像 + `jobs.json` 独立槽位 + `IHeartbeatService` 上层入口」，让定时任务与心跳共用一套执行设施但互不进入对方的集合。

**Architecture:** 内核只认一种 Job（`AgentJob`），执行身份由 `ScheduleRunProfile` 随 JobData 下发；缺画像时等价今天的 cron 行为。投递器按 `DispatchLayout` 决定写回布局。`JobsFile.Heartbeat` 是独立槽位，定时任务面（`Jobs`）结构性看不到心跳。心跳的外部 API 只有 `IHeartbeatService`。

**Tech Stack:** .NET 10、Quartz.NET（SQLite 持久化）、System.Text.Json、Blazor Server + AntDesign、xUnit v3 + Moq + FluentAssertions。

## Global Constraints

- 能力包只依赖 `Abstractions` + 原语；`Seeing.Agent.Scheduler` 不得引用 `Seeing.Agent.Core` 具体类型。
- Quartz、`jobs.json`、`jobs_history`、`_runningJobs` 只能由 `ScheduleManager` 写。`IHeartbeatService` 内部转调管理器，不自己注册触发器。
- `_heartbeat` 是保留 id：不能经 `CreateOrReplaceJobAsync` / `DeleteJobAsync` / `SetJobIntentAsync` 进入 `Jobs`；`RunJobOnceAsync("_heartbeat")` 返回 `TriggerResult.NotFound`。
- `cron_*` 工具与 `/cron-jobs` 页面不得返回或展示心跳。
- 心跳结果写入目标会话时只落一条 `Presentation=Assistant` 消息，不得留下未读的 `Presentation=System` 输入。
- 不做配置迁移：`Scheduler.Heartbeat` 删除后被忽略。
- 测试禁止固定时长等待。等状态用条件轮询（命中即返回），等事件用 `TaskCompletionSource`。现有 `WaitUntilAsync` 是允许的轮询实现。
- 测试一律用 VSTest 跑：`dotnet vstest <dll> --TestCaseFilter:"FullyQualifiedName~<Filter>"`。
- 提交信息用中文 `type(scope): 描述`。

## 文件结构

| 文件 | 责任 |
|---|---|
| `src/abstractions/.../Scheduling/DispatchLayout.cs` | 新增：写回布局枚举 |
| `src/abstractions/.../Scheduling/IScheduledJobDispatcher.cs` | 修改：`DispatchRequest` 增 `ReminderKind` / `SenderId` / `Layout` |
| `src/capabilities/Seeing.Agent.Scheduler/Models/ScheduleRunProfile.cs` | 新增：一次执行的身份 |
| `src/capabilities/Seeing.Agent.Scheduler/Models/SchedulerModels.cs` | 修改：删 `HeartbeatOptions` / `ActiveHoursOptions`、`JobsFile` 增 `Heartbeat` 槽位 |
| `src/capabilities/Seeing.Agent.Scheduler/Jobs/JobDataKeys.cs` | 修改：增 `RunProfile` / `LastRunAt`，删 `ActiveHours` / `HeartbeatTarget` |
| `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs` | 修改：读画像，不再写死 cron |
| `src/capabilities/Seeing.Agent.Scheduler/Jobs/HeartbeatJob.cs` | 删除 |
| `src/capabilities/Seeing.Agent.Scheduler/Jobs/ScheduleConversationResolver.cs` | 修改：按画像新建会话（前缀、标题、Origin） |
| `src/capabilities/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs` | 修改：`SessionScheduleDispatcher` 按布局分支 |
| `src/capabilities/Seeing.Agent.Scheduler/Management/ScheduleManager.cs` | 修改：保留 id、槽位读写、心跳注册、状态查找、历史 |
| `src/capabilities/Seeing.Agent.Scheduler/Management/HeartbeatService.cs` | 新增：`IHeartbeatService` 实现 |
| `src/capabilities/Seeing.Agent.Scheduler/Abstractions/SchedulerAbstractions.cs` | 修改：删 `ReloadHeartbeatAsync`；新增 `IHeartbeatService` 与两个 DTO |
| `src/capabilities/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs` | 修改：只注册 `AgentJob`、计数排除心跳、interval 锚点、删除前可初始化 |
| `src/capabilities/Seeing.Agent.Scheduler/Engine/ActiveHoursChecker.cs` | 删除 |
| `src/capabilities/Seeing.Agent.Scheduler/SchedulerConstants.cs` | 修改：删 `HeartbeatTargets` |
| `src/capabilities/Seeing.Agent.Scheduler/Tools/Heartbeat{Get,Update,Run}Tool.cs` | 新增：三个工具 |
| `src/capabilities/Seeing.Agent.Scheduler/Skills/heartbeat-management/SKILL.md` | 新增：技能 |
| `src/capabilities/Seeing.Agent.Scheduler/SchedulerModule.cs` | 修改：挂载三个工具 |
| `src/capabilities/Seeing.Agent.Scheduler/Commands/SchedulerCommands.cs` | 修改：`/heartbeat-run` 走服务 |
| `src/primitives/Seeing.Session/Core/ISessionManager.cs` | 修改：`EnsureSessionAsync` 增 `origin` |
| `src/primitives/Seeing.Session/Management/SessionManager.cs` | 修改：新建时应用 `origin` |
| `samples/Seeing.Agent.WebUI/Pages/HeartbeatPage.razor` | 重写：状态 / 配置 / 历史 |
| `samples/Seeing.Agent.WebUI/Pages/CronJobsPage.razor` | 修改：通知按 `Jobs` 过滤 |
| `samples/Seeing.Agent.WebUI/Components/Scheduler/SchedulerSettingsCard.razor` | 修改：去掉心跳重载 |

---

### Task 1: 执行画像与投递布局（无行为变更）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Scheduling/DispatchLayout.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Scheduling/IScheduledJobDispatcher.cs`（`DispatchRequest`）
- Create: `src/capabilities/Seeing.Agent.Scheduler/Models/ScheduleRunProfile.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/JobDataKeys.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/ScheduleRunProfileTests.cs`

**Interfaces:**
- Produces: `DispatchLayout { PromptAndResult, AssistantOnly }`；`DispatchRequest.ReminderKind` / `.SenderId` / `.Layout`；`ScheduleRunProfile`（属性见下）与 `ScheduleRunProfile.Cron` / `.Heartbeat`；`JobDataKeys.RunProfile = "runProfile"`、`JobDataKeys.LastRunAt = "lastRunAt"`。

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/ScheduleRunProfileTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Models;
using Seeing.Serialization;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ScheduleRunProfileTests
{
    [Fact]
    public void Cron_Defaults_MatchTodaysCronBehaviour()
    {
        var profile = ScheduleRunProfile.Cron;

        profile.Source.Should().Be(ScheduleSources.Cron);
        profile.ReminderKind.Should().Be(SystemReminder.Kinds.Cron);
        profile.SenderId.Should().Be("cron");
        profile.SessionKeyPrefix.Should().Be("cron");
        profile.OriginWhenCreated.Should().Be(SessionOrigin.Scheduler);
        profile.Layout.Should().Be(DispatchLayout.PromptAndResult);
    }

    [Fact]
    public void Heartbeat_Profile_UsesHeartbeatIdentityAndAssistantOnlyLayout()
    {
        var profile = ScheduleRunProfile.Heartbeat;

        profile.Source.Should().Be(ScheduleSources.Heartbeat);
        profile.ReminderKind.Should().Be(SystemReminder.Kinds.Heartbeat);
        profile.SenderId.Should().Be("heartbeat");
        profile.SessionKeyPrefix.Should().Be("hb");
        profile.OriginWhenCreated.Should().Be(SessionOrigin.Heartbeat);
        profile.Layout.Should().Be(DispatchLayout.AssistantOnly);
    }

    [Fact]
    public void Profile_RoundTrips_ThroughJson()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            ScheduleRunProfile.Heartbeat, SeeingJson.Readable);
        var back = System.Text.Json.JsonSerializer.Deserialize<ScheduleRunProfile>(json);

        back.Should().NotBeNull();
        back!.Source.Should().Be(ScheduleSources.Heartbeat);
        back.Layout.Should().Be(DispatchLayout.AssistantOnly);
        back.OriginWhenCreated.Should().Be(SessionOrigin.Heartbeat);
    }

    [Fact]
    public void DispatchRequest_Defaults_StayOnCron()
    {
        var request = new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "x"
        };

        request.ReminderKind.Should().Be(SystemReminder.Kinds.Cron);
        request.SenderId.Should().Be("cron");
        request.Layout.Should().Be(DispatchLayout.PromptAndResult);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
```
Expected: 编译失败，`ScheduleRunProfile` / `DispatchLayout` 不存在。

- [ ] **Step 3: 新增 `DispatchLayout`**

`src/abstractions/Seeing.Agent.Abstractions/Scheduling/DispatchLayout.cs`：

```csharp
namespace Seeing.Agent.Abstractions.Scheduling;

/// <summary>调度结果的会话写回布局。</summary>
public enum DispatchLayout
{
    /// <summary>先写一条任务提示（未读，下次 Wake 会带上），再写一条已消费的助手结果。cron 现状。</summary>
    PromptAndResult = 0,

    /// <summary>只写一条已消费的助手结果。心跳使用，避免在目标会话留下未读输入。</summary>
    AssistantOnly = 1
}
```

- [ ] **Step 4: `DispatchRequest` 增三个字段**

`src/abstractions/Seeing.Agent.Abstractions/Scheduling/IScheduledJobDispatcher.cs`，在 `Conversation` 之后插入：

```csharp
    /// <summary>提醒信封 kind（cron / heartbeat）。系统提示与助手结果都按它标记。</summary>
    public string ReminderKind { get; init; } = SystemReminder.Kinds.Cron;

    /// <summary>系统发送者标签（cron / heartbeat）。写入会话消息的 Sender.Id。</summary>
    public string SenderId { get; init; } = "cron";

    /// <summary>会话写回布局。默认 PromptAndResult，与既有 cron 行为一致。</summary>
    public DispatchLayout Layout { get; init; } = DispatchLayout.PromptAndResult;
```

文件顶部加 `using Seeing.Agent.Abstractions.Reminders;`。

- [ ] **Step 5: 新增 `ScheduleRunProfile`**

`src/capabilities/Seeing.Agent.Scheduler/Models/ScheduleRunProfile.cs`：

```csharp
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Models;

/// <summary>
/// 一次调度执行的身份画像。
/// <para>
/// 随 JobData 下发，由 <c>AgentJob</c> 读取。缺画像时等于 <see cref="Cron"/>，
/// 用户任务的既有行为（Hook 点、来源、提醒 kind、写回布局）完全不变。
/// </para>
/// </summary>
public sealed class ScheduleRunProfile
{
    /// <summary>执行来源（cron / heartbeat）。写入结果、Hook 载荷与出站来源。</summary>
    public string Source { get; set; } = ScheduleSources.Cron;

    /// <summary>提醒信封 kind。</summary>
    public string ReminderKind { get; set; } = SystemReminder.Kinds.Cron;

    /// <summary>会话消息的系统发送者标签。</summary>
    public string SenderId { get; set; } = "cron";

    /// <summary>仅 conversation 时新建会话的 id 前缀。</summary>
    public string SessionKeyPrefix { get; set; } = "cron";

    /// <summary>新建会话标题前缀。</summary>
    public string SessionTitlePrefix { get; set; } = "⏰";

    /// <summary>新建会话时写入的来源标记。</summary>
    public SessionOrigin OriginWhenCreated { get; set; } = SessionOrigin.Scheduler;

    /// <summary>会话写回布局。</summary>
    public DispatchLayout Layout { get; set; } = DispatchLayout.PromptAndResult;

    /// <summary>定时任务画像（默认）。</summary>
    public static ScheduleRunProfile Cron => new();

    /// <summary>心跳画像：独立的来源、提醒 kind、发送者、会话前缀与来源标记，只写助手结果。</summary>
    public static ScheduleRunProfile Heartbeat => new()
    {
        Source = ScheduleSources.Heartbeat,
        ReminderKind = SystemReminder.Kinds.Heartbeat,
        SenderId = "heartbeat",
        SessionKeyPrefix = "hb",
        SessionTitlePrefix = "💓",
        OriginWhenCreated = SessionOrigin.Heartbeat,
        Layout = DispatchLayout.AssistantOnly
    };
}
```

- [ ] **Step 6: 新增 JobData 键**

`src/capabilities/Seeing.Agent.Scheduler/Jobs/JobDataKeys.cs`，在 `Source` / `RunId` 之后加：

```csharp
    /// <summary>执行画像 JSON（<see cref="Models.ScheduleRunProfile"/>）；缺键等于 cron 画像。</summary>
    public const string RunProfile = "runProfile";

    /// <summary>上次执行结束时间（本地墙钟 ISO8601）；存在时作为 interval 触发器的起算锚点。</summary>
    public const string LastRunAt = "lastRunAt";
```

- [ ] **Step 7: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~ScheduleRunProfileTests"
```
Expected: 4 个用例全绿。

- [ ] **Step 8: 提交**

```powershell
git add src/abstractions/Seeing.Agent.Abstractions/Scheduling src/capabilities/Seeing.Agent.Scheduler/Models/ScheduleRunProfile.cs src/capabilities/Seeing.Agent.Scheduler/Jobs/JobDataKeys.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/ScheduleRunProfileTests.cs
git commit -m "feat(scheduler): 新增执行画像与投递布局契约"
```

---

### Task 2: `AgentJob` 读画像，不再写死 cron

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/ScheduleConversationResolver.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/AgentJobRunProfileTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ScheduleRunProfile`、`JobDataKeys.RunProfile`、`DispatchRequest.Layout` / `.ReminderKind` / `.SenderId`。
- Produces: `AgentJob` 对缺画像任务仍按 cron 执行；`ScheduleConversationResolver.ResolveSessionIdAsync(IServiceProvider, JobDataMap, string jobId, string? jobName, string? configuredSessionId, ScheduleRunProfile profile, CancellationToken)`。

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/AgentJobRunProfileTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Models;
using Seeing.Serialization;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class AgentJobRunProfileTests
{
    [Fact]
    public async Task Execute_WithoutProfile_KeepsCronIdentity()
    {
        var captured = new List<(IReadOnlyDictionary<string, object?> Payload, string? Kind)>();
        var (job, dispatched) = Build(profile: null, captured);

        await job.Execute(CreateContext(new JobDataMap { ["prompt"] = "跑一次", ["sessionId"] = "s1" }).Object);

        dispatched.Should().ContainSingle();
        dispatched[0].Source.Should().Be(ScheduleSources.Cron);
        dispatched[0].Layout.Should().Be(DispatchLayout.PromptAndResult);
        dispatched[0].ReminderKind.Should().Be(SystemReminder.Kinds.Cron);
    }

    [Fact]
    public async Task Execute_WithHeartbeatProfile_UsesHeartbeatIdentityAndAssistantOnly()
    {
        var captured = new List<(IReadOnlyDictionary<string, object?> Payload, string? Kind)>();
        var (job, dispatched) = Build(ScheduleRunProfile.Heartbeat, captured);

        await job.Execute(CreateContext(new JobDataMap { ["prompt"] = "报时", ["sessionId"] = "s1" }).Object);

        dispatched.Should().ContainSingle();
        dispatched[0].Source.Should().Be(ScheduleSources.Heartbeat);
        dispatched[0].Layout.Should().Be(DispatchLayout.AssistantOnly);
        dispatched[0].ReminderKind.Should().Be(SystemReminder.Kinds.Heartbeat);
        dispatched[0].SenderId.Should().Be("heartbeat");

        captured.Should().NotBeEmpty();
        captured.Should().OnlyContain(c => Equals(c.Payload["source"], ScheduleSources.Heartbeat));
        captured.Should().OnlyContain(c => c.Kind == SystemReminder.Kinds.Heartbeat);
    }

    private static (AgentJob Job, List<DispatchRequest> Dispatched) Build(
        ScheduleRunProfile? profile,
        List<(IReadOnlyDictionary<string, object?> Payload, string? Kind)> captured)
    {
        s_pendingProfile = profile;
        var dispatched = new List<DispatchRequest>();

        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(x => x.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> messages, AgentContext _, CancellationToken _) =>
                Stream(messages[0].Content, captured));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native });

        var selection = new Mock<IAgentSelectionResolver>();
        selection.Setup(r => r.ResolveAgentIdAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("build");

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.GetProjectRoot()).Returns("root");

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns("root");

        var hooks = new Mock<IHookManager>();
        hooks.Setup(h => h.TriggerBlockingAsync(
                It.IsAny<HookSpec>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(async (HookSpec _, string _, IReadOnlyDictionary<string, object?> payload,
                IDictionary<string, object?> _, CancellationToken _) =>
            {
                captured.Add((payload, null));
                await Task.CompletedTask;
                return new HookResult(Continue: true);
            });

        var reminders = new Mock<ISystemReminderRenderer>();
        reminders.Setup(r => r.Wrap(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string text, string source, string kind) =>
            {
                captured.Add((new Dictionary<string, object?>(), kind));
                return $"<envelope kind=\"{kind}\">{text}</envelope>";
            });

        var dispatcher = new Mock<IScheduledJobDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DispatchRequest, CancellationToken>((r, _) => dispatched.Add(r))
            .ReturnsAsync(DispatchResult.Ok());

        var services = new ServiceCollection().BuildServiceProvider();

        var job = new AgentJob(
            executor.Object, registry.Object, selection.Object, workspace.Object, world.Object,
            services, hooks.Object, reminders.Object, dispatcher.Object, [],
            NullLogger<AgentJob>.Instance);

        return (job, dispatched);
    }

    private static Mock<IJobExecutionContext> CreateContext(JobDataMap data)
    {
        if (data.ContainsKey(JobDataKeys.RunProfile) is false && s_pendingProfile is not null)
            data[JobDataKeys.RunProfile] = System.Text.Json.JsonSerializer.Serialize(s_pendingProfile, SeeingJson.Readable);

        var ctx = new Mock<IJobExecutionContext>();
        ctx.SetupAllProperties();
        ctx.Setup(c => c.MergedJobDataMap).Returns(data);
        ctx.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        ctx.Setup(c => c.FireTimeUtc).Returns(DateTimeOffset.UtcNow);
        ctx.Setup(c => c.JobDetail).Returns(new JobDetailImpl("job", "g", typeof(AgentJob)));
        return ctx;
    }

    private static ScheduleRunProfile? s_pendingProfile;

    private static async IAsyncEnumerable<IMessageEvent> Stream(
        string prompt,
        List<(IReadOnlyDictionary<string, object?> Payload, string? Kind)> _)
    {
        await Task.CompletedTask;
        yield return new StreamCompleteEvent
        {
            SessionId = "s1",
            Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
        };
    }
}
```

> 该测试用 `s_pendingProfile` 把画像塞进 JobDataMap：`Build` 的第一行已经赋值，`CreateContext` 读它。

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentJobRunProfileTests"
```
Expected: 第二个用例失败（`Source` 仍是 `cron`，`Layout` 仍是 `PromptAndResult`）。

- [ ] **Step 3: `AgentJob` 读画像**

`src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs`：

在 `Execute` 里 `var data = context.MergedJobDataMap;` 之后插入：

```csharp
        // 执行画像：缺键等于 cron。心跳与定时任务共用本 Job，身份差异全部由画像承载。
        var profile = data.GetJsonValue<ScheduleRunProfile>(JobDataKeys.RunProfile)
                      ?? ScheduleRunProfile.Cron;
```

把该文件里**所有** `ScheduleSources.Cron` 替换为 `profile.Source`（`Execute` 内需把 `profile` 一路传给 `ExecuteTextJobAsync` / `ExecuteAgentJobAsync`：给这两个方法加 `ScheduleRunProfile profile` 参数）。

`ExecuteAgentJobAsync` 里：

```csharp
        var metadata = new Dictionary<string, object>
        {
            ["source"] = profile.Source
        };
```

```csharp
                Content = _reminders.Wrap(
                    prompt,
                    SystemReminder.Sources.Job,
                    profile.ReminderKind)
```

`DispatchAsync` 改为接收画像：

```csharp
    private async Task DispatchAsync(
        JobDataMap data,
        string content,
        string defaultSessionId,
        string? userInput,
        ScheduleRunProfile profile,
        CancellationToken ct)
    {
        var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId) ?? defaultSessionId;

        var dispatchResult = await _dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = profile.Source,
            TaskType = ScheduleTaskTypes.Agent,
            Content = content,
            UserInput = userInput,
            SessionId = dispatchSessionId,
            ReminderKind = profile.ReminderKind,
            SenderId = profile.SenderId,
            Layout = profile.Layout,
            Conversation = ScheduleConversationResolver.ReadConversation(data)
        }, ct);

        if (!dispatchResult.Success)
        {
            _logger.LogWarning("Dispatch failed: {Error}", dispatchResult.Error);
        }
    }
```

`WrapJobUserInput` 也用画像的 kind：

```csharp
    private string? WrapJobUserInput(string? userInput, ScheduleRunProfile profile)
    {
        if (string.IsNullOrEmpty(userInput))
            return userInput;
        if (_reminders.TryParse(userInput, out _))
            return userInput;
        return _reminders.Wrap(userInput, SystemReminder.Sources.Job, profile.ReminderKind);
    }
```

调用点：`await DispatchAsync(data, result.Output, sessionId, wrappedInput, profile, timeoutCts.Token);`。

`ResolveSessionIdAsync` 调用加 `profile` 参数：

```csharp
        var sessionId = await ScheduleConversationResolver.ResolveSessionIdAsync(
            _services,
            data,
            jobId,
            data.GetStringValue(JobDataKeys.JobName),
            configuredSessionId,
            profile,
            timeoutCts.Token).ConfigureAwait(false);
```

- [ ] **Step 4: `ScheduleConversationResolver` 按画像新建会话**

`src/capabilities/Seeing.Agent.Scheduler/Jobs/ScheduleConversationResolver.cs`：`ResolveSessionIdAsync` 增加 `ScheduleRunProfile profile` 参数（放在 `configuredSessionId` 之后），新建会话段改成：

```csharp
        // 仅 conversation：每次触发新建 Root 会话并挂上（每轮独立上下文，避免粘性会话历史膨胀）
        // id = 画像前缀（可读/可排序）+ 时间戳 + 短随机后缀（同秒多次触发唯一）
        var newSessionId = $"{profile.SessionKeyPrefix}_{jobId}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        await sessionManager.EnsureSessionAsync(newSessionId).ConfigureAwait(false);
        await AttachRouteAsync(sessionManager, newSessionId, conversation).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(jobName))
        {
            await sessionManager.SetTitleAsync(
                newSessionId,
                $"{profile.SessionTitlePrefix} {jobName} {DateTime.Now:HH:mm}",
                ct).ConfigureAwait(false);
        }
```

> 本步**不要写 `origin:`**：`EnsureSessionAsync` 的 `origin` 参数在 Task 8 才加，现在写会导致编译失败。Task 8 的 Step 4 会把这一行补成 `EnsureSessionAsync(newSessionId, origin: profile.OriginWhenCreated)`。

- [ ] **Step 5: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentJobRunProfileTests"
```
Expected: 2 个用例绿。

- [ ] **Step 6: 跑既有调度器测试确认无回归**

```powershell
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~SchedulerIntegrationTests|FullyQualifiedName~AgentJobCatalogBaselineTests|FullyQualifiedName~AgentJobToolSchemasTests"
```
Expected: 全绿（cron 行为未变）。

- [ ] **Step 7: 提交**

```powershell
git add src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs src/capabilities/Seeing.Agent.Scheduler/Jobs/ScheduleConversationResolver.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/AgentJobRunProfileTests.cs
git commit -m "refactor(scheduler): AgentJob 改读执行画像，去掉写死的 cron 身份"
```

---

### Task 3: 投递器按布局分支

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/Execution/SessionScheduleDispatcherLayoutTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `DispatchLayout` 与 `DispatchRequest` 新字段。
- Produces: `AssistantOnly` 只投一条 `InboxPresentation.Assistant`；`PromptAndResult` 行为与本任务前完全一致。

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/Execution/SessionScheduleDispatcherLayoutTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Execution;

public class SessionScheduleDispatcherLayoutTests
{
    [Fact]
    public async Task AssistantOnly_WritesSingleConsumedAssistantMessage()
    {
        var delivered = new List<InboxMessage>();
        var dispatcher = Build(delivered);

        await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Heartbeat,
            TaskType = ScheduleTaskTypes.Agent,
            Content = "一切正常",
            UserInput = "检查状态",
            SessionId = "s1",
            ReminderKind = SystemReminder.Kinds.Heartbeat,
            SenderId = "heartbeat",
            Layout = DispatchLayout.AssistantOnly
        });

        delivered.Should().ContainSingle();
        delivered[0].Presentation.Should().Be(InboxPresentation.Assistant);
        delivered[0].Delivery.Should().Be(InboxDelivery.Inject);
        delivered[0].Kind.Should().Be(SystemReminder.Kinds.Heartbeat);
        delivered[0].Sender.Id.Should().Be("heartbeat");
        delivered[0].Body.Should().Be("一切正常");
    }

    [Fact]
    public async Task PromptAndResult_KeepsPromptThenAssistant()
    {
        var delivered = new List<InboxMessage>();
        var dispatcher = Build(delivered);

        await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Cron,
            TaskType = ScheduleTaskTypes.Agent,
            Content = "结果",
            UserInput = "<system-reminder source=\"job\" kind=\"cron\"><task>跑一次</task></system-reminder>",
            SessionId = "s1"
        });

        delivered.Should().HaveCount(2);
        delivered[0].Presentation.Should().Be(InboxPresentation.System);
        delivered[0].Kind.Should().Be(SystemReminder.Kinds.Cron);
        delivered[1].Presentation.Should().Be(InboxPresentation.Assistant);
        delivered[1].Kind.Should().Be(SystemReminder.Kinds.Cron);
        delivered[1].Sender.Id.Should().Be("cron");
    }

    private static SessionScheduleDispatcher Build(List<InboxMessage> delivered)
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime.Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((m, _) => delivered.Add(m))
            .ReturnsAsync(InboxResult.Ok("m1"));

        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.EnsureSessionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new SessionData { Id = "s1" });

        // 真实渲染器：PromptAndResult 用例要验证信封解包后的 kind 透传
        var reminders = new Seeing.Agent.Core.Reminders.SystemReminderRendererService();

        return new SessionScheduleDispatcher(
            runtime.Object, sessions.Object, reminders, NullLogger<SessionScheduleDispatcher>.Instance);
    }
}
```

> 若 `SystemReminderRendererService` 需要构造参数，改用 `Mock.Of<ISystemReminderRenderer>()` 并把 `TryParse` 设为 `false`；此时 `PromptAndResult` 用例只断言 `Kind` 等于 `ReminderKind`、`Body` 等于原 `UserInput`（不做信封解包断言）。

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionScheduleDispatcherLayoutTests"
```
Expected: `AssistantOnly` 用例失败（收到 2 条消息）。

- [ ] **Step 3: 改 `SessionScheduleDispatcher`**

`src/capabilities/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs`，把 `DispatchAsync` 主体替换为：

```csharp
    public async Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.SessionId))
            return DispatchResult.Ok();

        try
        {
            await _sessionManager.EnsureSessionAsync(request.SessionId)
                .ConfigureAwait(false);

            if (request.Layout == DispatchLayout.PromptAndResult && !string.IsNullOrEmpty(request.UserInput))
            {
                // 调用方已包裹为 system-reminder 信封；此处解包后由运行时以 Presentation=System
                // 统一重渲染（避免双重包裹），并沿用原信封的 source/kind。
                var source = SystemReminder.Sources.Job;
                var kind = request.ReminderKind;
                var body = request.UserInput;
                if (_reminders.TryParse(request.UserInput!, out var parts))
                {
                    body = parts.Task;
                    if (!string.IsNullOrEmpty(parts.Source))
                        source = parts.Source;
                    if (!string.IsNullOrEmpty(parts.Kind))
                        kind = parts.Kind;
                }

                await _runtime.DeliverAsync(new InboxMessage
                {
                    TargetSessionId = request.SessionId!,
                    Sender = new InboxSender { Kind = InboxSenderKind.System, Id = request.SenderId },
                    Body = body,
                    Delivery = InboxDelivery.Inject,
                    Presentation = InboxPresentation.System,
                    SourceTag = source,
                    Kind = kind
                }, ct).ConfigureAwait(false);
            }

            // 助手响应：外部产出写回，落盘即 consumed，不唤醒。
            // AssistantOnly（心跳）只走这一条，绝不在目标会话留下未读系统输入。
            await _runtime.DeliverAsync(new InboxMessage
            {
                TargetSessionId = request.SessionId!,
                Sender = new InboxSender { Kind = InboxSenderKind.System, Id = request.SenderId },
                Body = request.Content,
                Delivery = InboxDelivery.Inject,
                Presentation = InboxPresentation.Assistant,
                SourceTag = SystemReminder.Sources.Job,
                Kind = request.ReminderKind
            }, ct).ConfigureAwait(false);

            return DispatchResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch to session {SessionId}", request.SessionId);
            return DispatchResult.Fail(ex.Message);
        }
    }
```

文件顶部加 `using Seeing.Agent.Abstractions.Scheduling;`（若已有则跳过）。

- [ ] **Step 4: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionScheduleDispatcherLayoutTests|FullyQualifiedName~CompositeScheduleDispatcherTests"
```
Expected: 全绿。

- [ ] **Step 5: 提交**

```powershell
git add src/capabilities/Seeing.Agent.Scheduler/Execution/ScheduleDispatchers.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/Execution/SessionScheduleDispatcherLayoutTests.cs
git commit -m "feat(scheduler): 投递器按布局区分提示+结果与仅助手结果"
```

---

### Task 4: 保留 id、状态查找、历史落盘、计数排除

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Management/ScheduleManager.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatReservedIdTests.cs`

**Interfaces:**
- Produces: `ScheduleIdPolicy.IsReserved(string?)`；`ScheduleManager.FindJob(string)`（private）同时覆盖 `Jobs` 与 `Heartbeat` 槽位；`GetJobStatusAsync("_heartbeat")` 返回真实状态；`RunJobOnceAsync("_heartbeat")` 返回 `NotFound`；`CreateOrReplaceJobAsync` / `DeleteJobAsync` / `SetJobIntentAsync` 对保留 id 抛 `InvalidOperationException`；引擎计数与全量状态排除 `_heartbeat`。

- [ ] **Step 1: 抽出可复用的测试工厂**

新建 `tests/capabilities/Seeing.Agent.Scheduler.Tests/Fixtures/SchedulerIntegrationTestsFactory.cs`：把 `SchedulerIntegrationTests.CreateScheduleManagerWithServices` 的方法体整体搬过来，签名改为：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Quartz.Impl;
using Quartz.Simpl;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;

namespace Seeing.Agent.Scheduler.Tests.Fixtures;

/// <summary>搭建真实 Quartz + ScheduleManager 的测试工厂，供多个用例类复用。</summary>
internal static class SchedulerIntegrationTestsFactory
{
    public static (ScheduleManager Manager, QuartzSchedulerEngine Engine, IServiceProvider Services) Create(
        SchedulerTestWorkspace ws,
        List<string>? executedPrompts = null,
        IScheduledJobDispatcher? dispatcher = null,
        SchedulerOptions? schedulerOptions = null)
    {
        // ← 此处粘贴 SchedulerIntegrationTests.CreateScheduleManagerWithServices 的原方法体，
        //   executedPrompts 参数改为 `executedPrompts ??= new List<string>();`
        //   末尾 return (manager, engine, services);
        throw new NotImplementedException("粘贴原方法体后删除本行");
    }

    public static (ScheduleManager Manager, QuartzSchedulerEngine Engine) CreatePair(
        SchedulerTestWorkspace ws,
        List<string>? executedPrompts = null,
        IScheduledJobDispatcher? dispatcher = null,
        SchedulerOptions? schedulerOptions = null)
    {
        var (manager, engine, _) = Create(ws, executedPrompts, dispatcher, schedulerOptions);
        return (manager, engine);
    }
}
```

`SchedulerIntegrationTests` 里的 `CreateScheduleManager` 与 `CreateScheduleManagerWithServices` 改为转调该工厂（保留本地私有包装，让现有用例不用改调用点）：

```csharp
    private static (ScheduleManager Manager, QuartzSchedulerEngine Engine, IServiceProvider ServiceProvider)
        CreateScheduleManagerWithServices(
            SchedulerTestWorkspace ws,
            List<string> executedPrompts,
            IScheduledJobDispatcher? dispatcher = null,
            SchedulerOptions? schedulerOptions = null)
        => Fixtures.SchedulerIntegrationTestsFactory.Create(ws, executedPrompts, dispatcher, schedulerOptions);
```

`PreconfiguredSchedulerFactory` 一并搬进工厂文件（它是方法的依赖），从 `SchedulerIntegrationTests` 删除。

- [ ] **Step 2: 跑既有测试确认重构没破坏**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~SchedulerIntegrationTests"
```
Expected: 与重构前相同的用例数量、全部通过（此时两条心跳用例仍通过）。

- [ ] **Step 3: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatReservedIdTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class HeartbeatReservedIdTests
{
    [Fact]
    public void IsReserved_IsTrueOnlyForHeartbeatId()
    {
        ScheduleIdPolicy.IsReserved(SchedulerConstants.HeartbeatJobId).Should().BeTrue();
        ScheduleIdPolicy.IsReserved("job_lunch").Should().BeFalse();
        ScheduleIdPolicy.IsReserved("_heartbeat2").Should().BeFalse();
    }

    [Fact]
    public async Task RunJobOnce_ForReservedId_ReturnsNotFound()
    {
        using var ws = new Fixtures.SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = Fixtures.SchedulerIntegrationTestsFactory.CreatePair(ws);

        await manager.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await manager.RunJobOnceAsync(
                SchedulerConstants.HeartbeatJobId, TestContext.Current.CancellationToken);
            result.Should().BeOfType<TriggerResult.NotFound>();
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CreateOrReplace_ForReservedId_Throws()
    {
        using var ws = new Fixtures.SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = Fixtures.SchedulerIntegrationTestsFactory.CreatePair(ws);

        var spec = new ScheduledJobSpec
        {
            Id = SchedulerConstants.HeartbeatJobId,
            Intent = ScheduleIntent.Active,
            Prompt = "x",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }
        };

        var act = () => manager.CreateOrReplaceJobAsync(spec, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~HeartbeatReservedIdTests"
```
Expected: 编译失败（`ScheduleIdPolicy` 不存在）。

- [ ] **Step 3: 新增保留 id 策略**

`src/capabilities/Seeing.Agent.Scheduler/SchedulerConstants.cs` 追加：

```csharp
/// <summary>任务 id 保留策略：下划线前缀 id 属于平台内部任务，不能经用户任务 API 进入 Jobs。</summary>
public static class ScheduleIdPolicy
{
    /// <summary>是否为平台保留 id（内部任务）。</summary>
    public static bool IsReserved(string? jobId)
        => string.Equals(jobId, SchedulerConstants.HeartbeatJobId, StringComparison.Ordinal);
}
```

- [ ] **Step 4: `ScheduleManager` 落实保留 id 与槽位查找**

新增私有方法（放在 `ComputeState` 之前）：

```csharp
    /// <summary>任务定义查找：用户任务集合优先，其次心跳槽位。仅供状态/历史回写使用。</summary>
    private ScheduledJobSpec? FindJob(string jobId)
        => _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId)
           ?? (_jobsFile.Heartbeat is { } hb && string.Equals(hb.Id, jobId, StringComparison.Ordinal) ? hb : null);
```

`GetJobStatusAsync` 第一段改为：

```csharp
        var job = FindJob(jobId);
        if (job == null)
        {
            return new JobStatus { JobId = jobId, State = JobState.Disabled };
        }
```

`RunJobOnceAsync` 开头加保留判定并**删除**整段 `if (jobId == SchedulerConstants.HeartbeatJobId) { ... }`：

```csharp
        // 内部任务（心跳）不提供按 id 触发；走 IHeartbeatService.RunNowAsync。
        if (ScheduleIdPolicy.IsReserved(jobId))
            return new TriggerResult.NotFound();
```

`CreateOrReplaceJobAsync` 开头：

```csharp
        if (ScheduleIdPolicy.IsReserved(job.Id))
            throw new InvalidOperationException(
                $"'{job.Id}' 是平台保留任务 id，不能通过用户任务 API 创建或替换。");
```

`DeleteJobAsync` 与 `SetJobIntentAsync` 开头各加：

```csharp
        if (ScheduleIdPolicy.IsReserved(jobId))
            throw new InvalidOperationException(
                $"'{jobId}' 是平台保留任务 id，不能通过用户任务 API 删除或改意图。");
```

`OnJobExecutedAsync`：

```csharp
        // 心跳历史同样落盘：_heartbeat.json 是合法文件名，内部任务不该缺执行记录。
        await _repository.AppendHistoryAsync(jobId, record, ct);
```

```csharp
        // 更新任务最后运行时间：槽位任务也要回写，否则 interval 锚点永远为空。
        var job = FindJob(jobId);
        if (job != null)
        {
            job.LastRunAt = record.StartedAt;
            await _repository.SaveAsync(_jobsFile, ct);
        }
```

- [ ] **Step 5: 引擎计数排除心跳**

`src/capabilities/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs`，`GetStatusAsync` 里：

```csharp
            var jobKeys = (await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct))
                .Where(k => !ScheduleIdPolicy.IsReserved(k.Name))
                .ToList();
            var runningJobs = (await _scheduler.GetCurrentlyExecutingJobs(ct))
                .Where(j => !ScheduleIdPolicy.IsReserved(j.JobDetail.Key.Name))
                .ToList();
```

`GetAllJobStatusesAsync` 里同样过滤：

```csharp
            var jobKeys = (await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct))
                .Where(k => !ScheduleIdPolicy.IsReserved(k.Name))
                .ToList();
```

- [ ] **Step 6: 删除失效的两条心跳集成用例**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/SchedulerIntegrationTests.cs`：删除 `Heartbeat_TriggeredViaJobId` 与 `ScheduleManager_ManualHeartbeatRunViaJobId` 两个用例。它们断言 `RunJobOnceAsync("_heartbeat")` 返回 `Accepted`，而本任务已把保留 id 改为 `NotFound`；心跳的立即执行改由 `IHeartbeatService` 覆盖（Task 7）。

- [ ] **Step 7: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~HeartbeatReservedIdTests|FullyQualifiedName~SchedulerIntegrationTests|FullyQualifiedName~SchedulerEngineTests"
```
Expected: 全绿。

- [ ] **Step 8: 提交**

```powershell
git add src/capabilities/Seeing.Agent.Scheduler/Management/ScheduleManager.cs src/capabilities/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs src/capabilities/Seeing.Agent.Scheduler/SchedulerConstants.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatReservedIdTests.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/Fixtures/SchedulerIntegrationTestsFactory.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/SchedulerIntegrationTests.cs
git commit -m "fix(scheduler): 保留 id 隔离、心跳状态可查、执行历史落盘、计数排除"
```

---

### Task 5: 心跳触发器的起算与启动前清理

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Management/ScheduleManager.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatTriggerAnchorTests.cs`

**Interfaces:**
- Consumes: `JobDataKeys.LastRunAt`（Task 1）。
- Produces: `QuartzSchedulerEngine.RemoveJobAsync` 在 `_scheduler == null` 时会先取实例（不再静默 return）；`UpsertJobAsync` 的 interval 触发器在 JobData 带 `lastRunAt` 时按「上次 + 间隔」起算并 `DoNothing`，否则维持 `now+5s` + `NextWithExistingCount`。

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatTriggerAnchorTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class HeartbeatTriggerAnchorTests
{
    [Fact]
    public async Task IntervalWithAnchor_StartsAtLastRunPlusInterval()
    {
        var engine = await CreateEngineAsync();
        var last = DateTime.Now.AddHours(-1);

        await engine.UpsertJobAsync(
            "_heartbeat",
            new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h", Timezone = TimeZoneInfo.Local.Id },
            ScheduleIntent.Active,
            new JobDataMap { [JobDataKeys.LastRunAt] = last.ToString("O") },
            TestContext.Current.CancellationToken);

        var next = await FirstNextFireAsync(engine);
        next.Should().BeCloseTo(last.AddHours(6), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task IntervalWithoutAnchor_StaysAtFiveSeconds()
    {
        var engine = await CreateEngineAsync();

        await engine.UpsertJobAsync(
            "job_x",
            new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h", Timezone = TimeZoneInfo.Local.Id },
            ScheduleIntent.Active,
            new JobDataMap(),
            TestContext.Current.CancellationToken);

        var next = await FirstNextFireAsync(engine);
        next.Should().BeCloseTo(DateTime.Now.AddSeconds(5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RemoveJob_OnFreshEngine_InitializesSchedulerInsteadOfNoOp()
    {
        // 未 Start 的引擎：早期实现里 _scheduler == null 会直接 return，
        // 于是持久化存储里的旧心跳触发器删不掉，升级后第一次启动会先按旧表 misfire。
        var engine = await CreateEngineAsync(start: false);

        var act = () => engine.RemoveJobAsync("_heartbeat", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        engine.Scheduler.Should().NotBeNull("删除必须先拿到实例，而不是静默返回");
    }

    private static async Task<DateTime> FirstNextFireAsync(QuartzSchedulerEngine engine)
    {
        var scheduler = engine.Scheduler!;
        var keys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
        var key = keys.Single();
        var triggers = await scheduler.GetTriggersOfJob(key);
        var next = triggers.Select(t => t.GetNextFireTimeUtc())
            .Where(t => t.HasValue)
            .Select(t => t!.Value.LocalDateTime)
            .Min();
        return next;
    }

    private static async Task<QuartzSchedulerEngine> CreateEngineAsync(bool start = true)
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "AnchorTest_" + Guid.NewGuid().ToString("N"),
            ["quartz.threadPool.threadCount"] = "2"
        };
        var factory = new StdSchedulerFactory(props);
        var scheduler = await factory.GetScheduler();
        var engine = new QuartzSchedulerEngine(new FixedFactory(scheduler), NullLogger<QuartzSchedulerEngine>.Instance);
        if (start)
            await engine.StartAsync(TestContext.Current.CancellationToken);
        return engine;
    }

    private sealed class FixedFactory : ISchedulerFactory
    {
        private readonly IScheduler _scheduler;
        public FixedFactory(IScheduler scheduler) => _scheduler = scheduler;
        public Task<IScheduler> GetScheduler(CancellationToken ct = default) => Task.FromResult(_scheduler);
        public Task<IScheduler?> GetScheduler(string name, CancellationToken ct = default) => Task.FromResult<IScheduler?>(_scheduler);
        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IScheduler>>(new[] { _scheduler });
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~HeartbeatTriggerAnchorTests"
```
Expected: 第一个用例失败（下次触发是 5 秒后）；第三个用例失败（`RemoveJobAsync` 在 `_scheduler == null` 时直接 return，`engine.Scheduler` 仍是 null）。

- [ ] **Step 3: 引擎支持锚点**

`QuartzSchedulerEngine.UpsertJobAsync`，在 `BuildTriggers` 调用处：

```csharp
            // interval 起算锚点：仅当 JobData 显式带 lastRunAt 时生效（心跳）。
            // 用户任务不写该键，行为保持启动后约 5 秒。
            var anchor = ReadAnchor(mergedData);

            // 创建触发器（可能多个 windows）
            var triggers = BuildTriggers(jobId, schedule, anchor);
```

新增私有方法：

```csharp
    private static DateTime? ReadAnchor(JobDataMap data)
    {
        var raw = data.ContainsKey(JobDataKeys.LastRunAt) ? data.GetString(JobDataKeys.LastRunAt) : null;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTime.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Local)
            : null;
    }
```

签名改成：

```csharp
    private IReadOnlyList<ITrigger> BuildTriggers(string jobId, ScheduleSpec schedule, DateTime? anchor)
```

窗口分支调用保持 `BuildWindowIntervalTriggers(jobId, schedule, timezone)`（窗口是锚点，不叠加 `lastRunAt`）；单触发器分支：

```csharp
        return new[] { BuildSingleTrigger(jobId, schedule, timezone, anchor) };
```

```csharp
    private ITrigger BuildSingleTrigger(
        string jobId,
        ScheduleSpec schedule,
        TimeZoneInfo timezone,
        DateTime? anchor)
```

interval 分支：

```csharp
            case ScheduleTypes.Interval:
                if (string.IsNullOrEmpty(schedule.Every))
                    throw new InvalidOperationException("Interval schedule requires 'every'");

                var interval = ScheduleExpressionParser.ParseInterval(schedule.Every);

                if (anchor is { } lastRun)
                {
                    // 有锚点：从「上次 + 间隔」起算；已逾期则从现在起算，绝不立刻补跑。
                    var next = lastRun.Add(interval);
                    var now = DateTime.Now;
                    triggerBuilder.StartAt(next > now ? next : now + interval);
                    triggerBuilder.WithSimpleSchedule(x => x
                        .WithInterval(interval)
                        .RepeatForever()
                        .WithMisfireHandlingInstructionDoNothing());
                }
                else
                {
                    triggerBuilder.StartAt(DateBuilder.FutureDate(5, IntervalUnit.Second));
                    triggerBuilder.WithSimpleSchedule(x => x
                        .WithInterval(interval)
                        .RepeatForever()
                        .WithMisfireHandlingInstructionNextWithExistingCount());
                }
                break;
```

> 原本 `var startNow = schedule.Type != ScheduleTypes.Interval;` 与 `if (startNow) triggerBuilder.StartNow(); else triggerBuilder.StartAt(FutureDate(5s));` 这两段要删掉；`once` / `cron` 分支各自保留自己的 `StartNow()` / `StartAt(runAt)`。
> 注意：`anchor` 是 `DateTime`（Local），`StartAt(DateTime)` 会按其 Kind 转 `DateTimeOffset`，无需手工换算。

同时 `RemoveJobAsync` 开头改成：

```csharp
        await _lock.WaitAsync(ct);
        try
        {
            // 启动前调用：必须先拿到实例才能删除持久化里的旧触发器。
            if (_scheduler == null)
            {
                _scheduler = await _schedulerFactory.GetScheduler(ct);
            }
```

（删掉原来的 `if (_scheduler == null) return;`）

- [ ] **Step 4: 启动前删除旧心跳触发器**

`ScheduleManager.StartAsync`，在 `await _engine.StartAsync(ct);` **之前**插入：

```csharp
            // 升级/重启兜底：先清掉持久化存储里的旧心跳触发器，避免它按旧计划 misfire 一次，
            // 随后又按新锚点触发一次。用户任务的持久化触发器不动。
            await _engine.RemoveJobAsync(SchedulerConstants.HeartbeatJobId, ct).ConfigureAwait(false);
```

- [ ] **Step 5: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~HeartbeatTriggerAnchorTests|FullyQualifiedName~SchedulerEngineTests"
```
Expected: 全绿。

- [ ] **Step 6: 提交**

```powershell
git add src/capabilities/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs src/capabilities/Seeing.Agent.Scheduler/Management/ScheduleManager.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatTriggerAnchorTests.cs
git commit -m "fix(scheduler): interval 支持 lastRunAt 锚点，启动前清理旧心跳触发器"
```

---

### Task 6: 心跳槽位替换旧配置，删除 `HeartbeatJob`

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Models/SchedulerModels.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/JobDataKeys.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Management/ScheduleManager.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Abstractions/SchedulerAbstractions.cs`
- Delete: `src/capabilities/Seeing.Agent.Scheduler/Jobs/HeartbeatJob.cs`
- Delete: `src/capabilities/Seeing.Agent.Scheduler/Engine/ActiveHoursChecker.cs`
- Modify: `tests/capabilities/Seeing.Agent.Scheduler.Tests/Fixtures/SchedulerTestWorkspace.cs`
- Modify: `tests/capabilities/Seeing.Agent.Scheduler.Tests/SchedulerOptionsProviderTests.cs`
- Modify: `tests/capabilities/Seeing.Agent.Scheduler.Tests/SchedulerIntegrationTests.cs`
- Modify: `tests/spine/Seeing.Agent.Tests/App/ExecutionRunnerContextAssemblyTests.cs`
- Delete: `tests/capabilities/Seeing.Agent.Scheduler.Tests/ScheduleExpressionParserTests.cs` 中 `ActiveHoursCheckerTests` 类
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatSlotTests.cs`

**Interfaces:**
- Produces: `JobsFile.Heartbeat`（`ScheduledJobSpec?`）；`ScheduleManager` 内部 `SaveHeartbeatAsync` / `HeartbeatSpec` / `RunHeartbeatAsync` / `CancelHeartbeatAsync`；`IScheduleManager` 去掉 `ReloadHeartbeatAsync`。

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatSlotTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Serialization;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class HeartbeatSlotTests
{
    [Fact]
    public void JobsFile_WithoutHeartbeatSlot_DeserializesToNull()
    {
        var json = """{"version":2,"jobs":[]}""";
        var file = System.Text.Json.JsonSerializer.Deserialize<JobsFile>(json);

        file.Should().NotBeNull();
        file!.Heartbeat.Should().BeNull();
        file.Jobs.Should().BeEmpty();
    }

    [Fact]
    public void JobsFile_RoundTripsHeartbeatSlot()
    {
        var file = new JobsFile
        {
            Heartbeat = new ScheduledJobSpec
            {
                Id = SchedulerConstants.HeartbeatJobId,
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Agent,
                Prompt = "报时",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h" },
                Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(file, SeeingJson.ReadableIndented);
        var back = System.Text.Json.JsonSerializer.Deserialize<JobsFile>(json);

        back!.Heartbeat.Should().NotBeNull();
        back.Heartbeat!.Prompt.Should().Be("报时");
        back.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveHeartbeat_RegistersQuartzJobWithHeartbeatProfile()
    {
        using var ws = new Fixtures.SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, engine) = Fixtures.SchedulerIntegrationTestsFactory.CreatePair(ws);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await manager.SaveHeartbeatAsync(new ScheduledJobSpec
            {
                Id = SchedulerConstants.HeartbeatJobId,
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Agent,
                Prompt = "报时",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h" },
                Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
            }, TestContext.Current.CancellationToken);

            var status = await manager.GetJobStatusAsync(
                SchedulerConstants.HeartbeatJobId, TestContext.Current.CancellationToken);
            status.State.Should().Be(JobState.Scheduled);

            var detail = await engine.Scheduler!.GetJobDetail(
                new Quartz.JobKey(SchedulerConstants.HeartbeatJobId, SchedulerConstants.DefaultJobGroup));
            detail.Should().NotBeNull();
            detail!.JobType.Should().Be(typeof(Scheduler.Jobs.AgentJob));

            var profileJson = detail.JobDataMap.GetString(Scheduler.Jobs.JobDataKeys.RunProfile);
            var profile = System.Text.Json.JsonSerializer.Deserialize<ScheduleRunProfile>(profileJson!);
            profile!.Source.Should().Be(ScheduleSources.Heartbeat);
            profile.Layout.Should().Be(DispatchLayout.AssistantOnly);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SaveHeartbeat_Disabled_RemovesQuartzJobButKeepsSlot()
    {
        using var ws = new Fixtures.SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = Fixtures.SchedulerIntegrationTestsFactory.CreatePair(ws);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await manager.SaveHeartbeatAsync(new ScheduledJobSpec
            {
                Id = SchedulerConstants.HeartbeatJobId,
                Intent = ScheduleIntent.Disabled,
                TaskType = ScheduleTaskTypes.Agent,
                Prompt = "报时",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h" }
            }, TestContext.Current.CancellationToken);

            var status = await manager.GetJobStatusAsync(
                SchedulerConstants.HeartbeatJobId, TestContext.Current.CancellationToken);
            status.State.Should().Be(JobState.Disabled);

            var repo = new JsonScheduleRepository(ws.Workspace, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonScheduleRepository>.Instance);
            var file = await repo.LoadAsync(TestContext.Current.CancellationToken);
            file.Heartbeat.Should().NotBeNull("禁用只移除触发器，槽位必须保留以便用户重新启用");
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
```
Expected: 编译失败（`JobsFile.Heartbeat` / `SaveHeartbeatAsync` 不存在）。

- [ ] **Step 3: 模型改槽位**

`src/capabilities/Seeing.Agent.Scheduler/Models/SchedulerModels.cs`：

`SchedulerOptions` 删 `Heartbeat` 属性。删掉 `HeartbeatOptions` 与 `ActiveHoursOptions` 两个类。`JobsFile` 改为：

```csharp
/// <summary>jobs.json 根对象</summary>
public class JobsFile
{
    /// <summary>版本号（用于迁移，默认 0 表示需要迁移）</summary>
    public int Version { get; set; } = 0;

    /// <summary>用户定时任务（定时任务面的唯一数据源）。</summary>
    public List<ScheduledJobSpec> Jobs { get; set; } = new();

    /// <summary>
    /// 心跳系统任务（id 固定 <c>_heartbeat</c>）。
    /// <para>
    /// 独立槽位而非 <see cref="Jobs"/> 成员：定时任务面（列表、工具、页面、统计）全部遍历
    /// <see cref="Jobs"/>，放这里即是结构性排除，不需要在六七个出口重复过滤谓词。
    /// </para>
    /// <para>null 表示用户尚未配置心跳：不注册触发器，也不自动生成默认配置。</para>
    /// </summary>
    public ScheduledJobSpec? Heartbeat { get; set; }

    /// <summary>迁移旧版本数据</summary>
    internal void MigrateIfNeeded()
    {
        if (Version < 2)
        {
            foreach (var job in Jobs)
                job.MigrateIntent();
            Version = 2;
        }
    }
}
```

`SchedulerConstants.cs` 删 `HeartbeatTargets` 静态类。

`JobDataKeys.cs` 删 `HeartbeatTarget` 与 `ActiveHours` 两个常量。

- [ ] **Step 4: 管理器按槽位注册**

`src/capabilities/Seeing.Agent.Scheduler/Management/ScheduleManager.cs`：

`StartAsync` 里 `await RegisterHeartbeatAsync(ct);` 保留调用；把 `RegisterHeartbeatAsync` 整体替换为：

```csharp
    /// <summary>心跳槽位快照（深拷贝）。null 表示尚未配置。</summary>
    internal ScheduledJobSpec? HeartbeatSpec()
        => _jobsFile.Heartbeat is null ? null : CloneSpec(_jobsFile.Heartbeat);

    /// <summary>保存心跳槽位：写盘，并按 intent 注册或移除触发器。</summary>
    internal async Task<ScheduledJobSpec> SaveHeartbeatAsync(ScheduledJobSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!string.Equals(spec.Id, SchedulerConstants.HeartbeatJobId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"心跳任务 id 必须为 '{SchedulerConstants.HeartbeatJobId}'，实际为 '{spec.Id}'。");

        var stored = CloneSpec(spec);
        stored.Id = SchedulerConstants.HeartbeatJobId;
        _jobsFile.Heartbeat = stored;
        await _repository.SaveAsync(_jobsFile, ct).ConfigureAwait(false);

        if (_started)
            await RegisterHeartbeatAsync(ct).ConfigureAwait(false);

        return CloneSpec(stored);
    }

    /// <summary>在槽位副本上就地修改并保存（供心跳服务做局部更新）。</summary>
    internal async Task<ScheduledJobSpec?> MutateHeartbeatAsync(
        Action<ScheduledJobSpec> mutate, CancellationToken ct = default)
    {
        if (_jobsFile.Heartbeat is null)
            return null;

        var copy = CloneSpec(_jobsFile.Heartbeat);
        mutate(copy);
        return await SaveHeartbeatAsync(copy, ct).ConfigureAwait(false);
    }

    /// <summary>立即执行心跳。未配置 / 未启动 / 已禁用 / 正在执行各有明确结果。</summary>
    internal async Task<TriggerResult> RunHeartbeatAsync(CancellationToken ct = default)
    {
        var hb = _jobsFile.Heartbeat;
        if (hb is null)
            return new TriggerResult.NotFound();

        if (!_started || _engine.Scheduler is null)
            return new TriggerResult.Disabled();

        if (hb.Intent != ScheduleIntent.Active)
            return new TriggerResult.Disabled();

        var runId = Guid.NewGuid().ToString("N");
        if (!_runningJobs.TryAdd(SchedulerConstants.HeartbeatJobId, new RunInfo(runId, DateTime.Now)))
            return new TriggerResult.Conflict("任务正在执行中");

        try
        {
            var additionalData = new JobDataMap { [JobDataKeys.Source] = ScheduleSources.Manual };
            await _engine.UpsertJobAsync(
                SchedulerConstants.HeartbeatJobId, hb.Schedule, hb.Intent, BuildHeartbeatData(hb), ct)
                .ConfigureAwait(false);
            await _engine.TriggerJobAsync(SchedulerConstants.HeartbeatJobId, runId, additionalData, ct)
                .ConfigureAwait(false);

            _logger.LogInformation("Heartbeat triggered with RunId={RunId}", runId);
            return new TriggerResult.Accepted(runId);
        }
        catch
        {
            _runningJobs.TryRemove(SchedulerConstants.HeartbeatJobId, out _);
            throw;
        }
    }

    /// <summary>取消正在执行的心跳。</summary>
    internal async Task<bool> CancelHeartbeatAsync(CancellationToken ct = default)
    {
        if (_jobsFile.Heartbeat is null)
            return false;

        return await CancelJobAsync(SchedulerConstants.HeartbeatJobId, ct).ConfigureAwait(false);
    }

    private async Task RegisterHeartbeatAsync(CancellationToken ct)
    {
        var hb = _jobsFile.Heartbeat;
        if (hb is null)
        {
            await _engine.RemoveJobAsync(SchedulerConstants.HeartbeatJobId, ct).ConfigureAwait(false);
            return;
        }

        await _engine.UpsertJobAsync(
            SchedulerConstants.HeartbeatJobId, hb.Schedule, hb.Intent, BuildHeartbeatData(hb), ct)
            .ConfigureAwait(false);
    }

    private JobDataMap BuildHeartbeatData(ScheduledJobSpec hb)
    {
        var data = new JobDataMap
        {
            [JobDataKeys.JobId] = SchedulerConstants.HeartbeatJobId,
            [JobDataKeys.JobName] = hb.Name ?? "心跳",
            [JobDataKeys.TaskType] = ScheduleTaskTypes.Agent,
            [JobDataKeys.Prompt] = hb.Prompt ?? string.Empty,
            [JobDataKeys.AgentId] = hb.Agent ?? string.Empty,
            [JobDataKeys.TimeoutSeconds] = hb.Runtime.TimeoutSeconds.ToString(),
            [JobDataKeys.Source] = ScheduleSources.Heartbeat
        };

        if (!string.IsNullOrWhiteSpace(hb.Dispatch?.Target?.SessionId))
            data[JobDataKeys.SessionId] = hb.Dispatch.Target.SessionId;

        if (!string.IsNullOrWhiteSpace(hb.Model))
            data[JobDataKeys.Model] = hb.Model;

        if (!string.IsNullOrWhiteSpace(hb.Mode))
            data[JobDataKeys.Mode] = hb.Mode;

        var conversation = hb.Dispatch?.Target?.Conversation;
        if (conversation is { IsComplete: true })
        {
            data[JobDataKeys.ConversationChannelId] = conversation.ChannelId!.Trim();
            data[JobDataKeys.ConversationKind] = conversation.Kind!.Trim().ToLowerInvariant();
            data[JobDataKeys.ConversationExternalId] = conversation.ExternalId!.Trim();
            if (!string.IsNullOrWhiteSpace(conversation.Title))
                data[JobDataKeys.ConversationTitle] = conversation.Title!.Trim();
        }

        data.SetJsonValue(JobDataKeys.RunProfile, ScheduleRunProfile.Heartbeat);

        // interval 锚点：上次执行时间决定下次触发，重启不重置节律。
        if (hb.LastRunAt is { } lastRun)
            data[JobDataKeys.LastRunAt] = lastRun.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        return data;
    }

    private static ScheduledJobSpec CloneSpec(ScheduledJobSpec spec)
        => System.Text.Json.JsonSerializer.Deserialize<ScheduledJobSpec>(
               System.Text.Json.JsonSerializer.Serialize(spec, SeeingJson.Readable),
               SeeingJson.Readable)
           ?? new ScheduledJobSpec();
```

`RegisterUserJobAsync` 里补画像：在 `await _engine.UpsertJobAsync(job.Id, job.Schedule, job.Intent, jobData, ct);` 之前加：

```csharp
        jobData.SetJsonValue(JobDataKeys.RunProfile, ScheduleRunProfile.Cron);

        if (job.LastRunAt is { } lastRun)
            jobData[JobDataKeys.LastRunAt] = lastRun.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
```

删除 `ReloadHeartbeatAsync` 方法。`IScheduleManager` 里删 `Task ReloadHeartbeatAsync(...)`。

`ScheduleManager.StartAsync` 里心跳注册加隔离：

```csharp
            // 注册用户任务（只注册 Active 状态的）；单个任务失败不阻断其余任务
            foreach (var job in _jobsFile.Jobs.Where(j => j.Intent == ScheduleIntent.Active))
            {
                try
                {
                    await RegisterUserJobAsync(job, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "注册定时任务失败，已跳过: JobId={JobId}", job.Id);
                }
            }

            // 注册心跳
            try
            {
                await RegisterHeartbeatAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "注册心跳失败，已跳过（不影响用户任务）");
            }
```

- [ ] **Step 5: 引擎只注册 `AgentJob`**

`QuartzSchedulerEngine.UpsertJobAsync`：

```csharp
            // 心跳与定时任务共用同一个 Job 类型；身份差异由执行画像承载。
            var jobType = typeof(Jobs.AgentJob);
```

删除 `HeartbeatJob.cs` 与 `ActiveHoursChecker.cs`。

- [ ] **Step 6: 修测试夹具与旧测试**

`tests/.../Fixtures/SchedulerTestWorkspace.cs`：

- `WriteSeeingJson` 的 JSON 模板删掉整个 `"Heartbeat": { ... }` 块（`Scheduler` 与 `DefaultAgent` 同级，注意保留逗号正确）。
- `CreateDefaultSchedulerOptions` 删 `Heartbeat = new HeartbeatOptions { ... }`。

`tests/.../SchedulerOptionsProviderTests.cs`：`Reload_LoadsSchedulerSectionFromProjectSeeingJson` 删除 `Heartbeat = new HeartbeatOptions { ... }` 与三行 `provider.Current.Heartbeat.*` 断言；第二个用例同样删除 `Heartbeat` 相关行。保留 `Enabled` / `Timezone` 断言。

`tests/.../SchedulerIntegrationTests.cs`：两条心跳用例已在 Task 4 删除；`CreateScheduleManagerWithServices` 中删除 `.AddTransient<HeartbeatJob>()`。

`tests/spine/Seeing.Agent.Tests/App/ExecutionRunnerContextAssemblyTests.cs`：把 `HeartbeatBypass_ShouldNotEmitDeltaMessage`（或同类用例）改为走 `AgentJob`：删除 `CreateHeartbeatJob(...)` 与 `CreateHeartbeatContextMock()`，改为 `CreateAgentJob(...)` + 在 JobDataMap 里塞 `JobDataKeys.RunProfile`（`ScheduleRunProfile.Heartbeat` 序列化）与 `prompt`；`JobDetail` 用 `typeof(AgentJob)`。断言不变（仍要求 `ReadOnly` 装配、不产生增量消息）。

`tests/.../ScheduleExpressionParserTests.cs`：删除文件中的 `ActiveHoursCheckerTests` 类（该类只测已删的 `ActiveHoursChecker`）。

- [ ] **Step 7: 跑全量调度器测试**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll
```
Expected: 全绿。

- [ ] **Step 8: 跑 spine 测试确认旁路装配无回归**

```powershell
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionRunnerContextAssemblyTests"
```
Expected: 全绿。

- [ ] **Step 9: 提交**

```powershell
git add -A
git commit -m "refactor(scheduler): 心跳改为 jobs.json 独立槽位，删除 HeartbeatJob 与 ActiveHours"
```

---

### Task 7: `IHeartbeatService`

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Abstractions/SchedulerAbstractions.cs`
- Create: `src/capabilities/Seeing.Agent.Scheduler/Management/HeartbeatService.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatServiceTests.cs`

**Interfaces:**
- Consumes: Task 6 的 `ScheduleManager.HeartbeatSpec` / `SaveHeartbeatAsync` / `MutateHeartbeatAsync` / `RunHeartbeatAsync` / `CancelHeartbeatAsync`，`IScheduleManager.GetJobStatusAsync`。
- Produces: `IHeartbeatService`、`HeartbeatSnapshot`、`HeartbeatUpdate`；DI 登记 `IHeartbeatService`。

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatServiceTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class HeartbeatServiceTests
{
    [Fact]
    public async Task Get_WhenNotConfigured_ReportsNotConfigured()
    {
        var (service, manager, _) = await BuildAsync();

        var snapshot = await service.GetAsync(TestContext.Current.CancellationToken);

        snapshot.Configured.Should().BeFalse();
        snapshot.Status.Should().BeNull();
        snapshot.LastRun.Should().BeNull();
    }

    [Fact]
    public async Task Update_PartialOnly_LeavesOtherFieldsUntouched()
    {
        var (service, _, _) = await BuildAsync();

        await service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "检查待办",
            Every = "6h",
            SessionId = "main"
        }, TestContext.Current.CancellationToken);

        var updated = await service.UpdateAsync(
            new HeartbeatUpdate { Enabled = false }, TestContext.Current.CancellationToken);

        updated.Spec!.Prompt.Should().Be("检查待办");
        updated.Spec.Schedule.Every.Should().Be("6h");
        updated.Spec.Dispatch.Target.SessionId.Should().Be("main");
        updated.Spec.Intent.Should().Be(ScheduleIntent.Disabled);
    }

    [Fact]
    public async Task Update_WithInvalidInterval_Throws()
    {
        var (service, _, _) = await BuildAsync();

        var act = () => service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "x",
            Every = "6 hours",
            SessionId = "main"
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Update_WithoutTarget_Throws()
    {
        var (service, _, _) = await BuildAsync();

        var act = () => service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "x",
            Every = "6h"
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunNow_NotConfigured_IsNotAccepted()
    {
        var (service, _, _) = await BuildAsync();

        var result = await service.RunNowAsync(TestContext.Current.CancellationToken);

        result.Should().NotBeOfType<TriggerResult.Accepted>();
    }

    [Fact]
    public async Task RunNow_SchedulerNotStarted_IsNotAccepted()
    {
        var (service, _, _) = await BuildAsync();
        await service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true, Prompt = "x", Every = "6h", SessionId = "main"
        }, TestContext.Current.CancellationToken);

        var result = await service.RunNowAsync(TestContext.Current.CancellationToken);

        result.Should().NotBeOfType<TriggerResult.Accepted>();
    }

    private static async Task<(IHeartbeatService Service, ScheduleManager Manager, Fixtures.SchedulerTestWorkspace Ws)>
        BuildAsync()
    {
        var ws = new Fixtures.SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = Fixtures.SchedulerIntegrationTestsFactory.CreatePair(ws);
        var service = new Seeing.Agent.Scheduler.Management.HeartbeatService(
            manager,
            new Seeing.Agent.Scheduler.Persistence.JsonScheduleRepository(
                ws.Workspace,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Seeing.Agent.Scheduler.Persistence.JsonScheduleRepository>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Seeing.Agent.Scheduler.Management.HeartbeatService>.Instance);
        await Task.CompletedTask;
        return (service, manager, ws);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
```
Expected: 编译失败（`IHeartbeatService` / `HeartbeatService` 不存在）。

- [ ] **Step 3: 定义契约**

`src/capabilities/Seeing.Agent.Scheduler/Abstractions/SchedulerAbstractions.cs` 追加：

```csharp
/// <summary>心跳的唯一外部入口。页面、heartbeat_* 工具与 /heartbeat-run 只依赖本接口。</summary>
public interface IHeartbeatService
{
    /// <summary>当前快照：槽位副本、真实调度状态、最近一次执行、调度器是否已启动。</summary>
    Task<HeartbeatSnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>局部更新；未提供的字段保持不变。校验失败抛 <see cref="ArgumentException"/>。</summary>
    Task<HeartbeatSnapshot> UpdateAsync(HeartbeatUpdate update, CancellationToken ct = default);

    /// <summary>立即执行一次。未配置/未启动/已禁用/正在执行各有明确结果。</summary>
    Task<TriggerResult> RunNowAsync(CancellationToken ct = default);

    /// <summary>取消正在执行的心跳。</summary>
    Task<bool> CancelAsync(CancellationToken ct = default);

    /// <summary>执行历史（倒序）。</summary>
    Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(
        int limit, int offset = 0, CancellationToken ct = default);
}

/// <summary>心跳快照。</summary>
/// <param name="Spec">槽位深拷贝；null 表示尚未配置。</param>
/// <param name="Status">Quartz 运行时状态；未配置时为 null。</param>
/// <param name="LastRun">最近一次执行记录；从未执行时为 null。</param>
/// <param name="SchedulerStarted">调度器是否已启动。</param>
public sealed record HeartbeatSnapshot(
    ScheduledJobSpec? Spec,
    JobStatus? Status,
    JobExecutionRecord? LastRun,
    bool SchedulerStarted)
{
    /// <summary>是否已配置。</summary>
    public bool Configured => Spec is not null;

    /// <summary>是否启用（已配置且 intent 为 Active）。</summary>
    public bool Enabled => Spec?.Intent == ScheduleIntent.Active;
}

/// <summary>心跳局部更新。未出现的字段保持不变。</summary>
public sealed record HeartbeatUpdate
{
    public bool? Enabled { get; init; }
    public string? Prompt { get; init; }
    public string? Every { get; init; }
    public IReadOnlyList<ScheduleWindow>? Windows { get; init; }
    public string? Timezone { get; init; }
    public string? SessionId { get; init; }
    public ConversationSpec? Conversation { get; init; }
    public string? Agent { get; init; }
    public string? Model { get; init; }
    public int? TimeoutSeconds { get; init; }
}
```

- [ ] **Step 4: 实现服务**

`src/capabilities/Seeing.Agent.Scheduler/Management/HeartbeatService.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Management;

/// <summary>
/// 心跳上层门面。所有写操作转调 <see cref="ScheduleManager"/>，
/// 本类不持有 Quartz 实例、不写 jobs.json，避免出现第二个调度真相源。
/// </summary>
public sealed class HeartbeatService : IHeartbeatService
{
    private const int DefaultLimit = 20;

    private readonly ScheduleManager _manager;
    private readonly IScheduleRepository _repository;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        ScheduleManager manager,
        IScheduleRepository repository,
        ILogger<HeartbeatService> logger)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HeartbeatSnapshot> GetAsync(CancellationToken ct = default)
    {
        var spec = _manager.HeartbeatSpec();
        if (spec is null)
            return new HeartbeatSnapshot(null, null, null, _manager.IsStarted);

        var status = await _manager.GetJobStatusAsync(SchedulerConstants.HeartbeatJobId, ct)
            .ConfigureAwait(false);
        var last = await _repository
            .GetHistoryAsync(SchedulerConstants.HeartbeatJobId, 1, 0, ct)
            .ConfigureAwait(false);

        return new HeartbeatSnapshot(spec, status, last.FirstOrDefault(), _manager.IsStarted);
    }

    /// <inheritdoc />
    public async Task<HeartbeatSnapshot> UpdateAsync(HeartbeatUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var existing = _manager.HeartbeatSpec();
        var spec = existing ?? CreateDefaultSpec();

        Apply(spec, update);
        Validate(spec);

        await _manager.SaveHeartbeatAsync(spec, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "心跳配置已保存 (Enabled={Enabled}, Every={Every})",
            spec.Intent == ScheduleIntent.Active, spec.Schedule.Every);

        return await GetAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TriggerResult> RunNowAsync(CancellationToken ct = default)
        => _manager.RunHeartbeatAsync(ct);

    /// <inheritdoc />
    public Task<bool> CancelAsync(CancellationToken ct = default)
        => _manager.CancelHeartbeatAsync(ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(
        int limit, int offset = 0, CancellationToken ct = default)
        => _repository.GetHistoryAsync(
            SchedulerConstants.HeartbeatJobId,
            Math.Max(1, Math.Min(limit <= 0 ? DefaultLimit : limit, 100)),
            Math.Max(0, offset),
            ct);

    private static ScheduledJobSpec CreateDefaultSpec() => new()
    {
        Id = SchedulerConstants.HeartbeatJobId,
        Name = "心跳",
        Intent = ScheduleIntent.Disabled,
        TaskType = ScheduleTaskTypes.Agent,
        Schedule = new ScheduleSpec
        {
            Type = ScheduleTypes.Interval,
            Every = "6h",
            Timezone = TimeZoneInfo.Local.Id
        },
        Dispatch = new DispatchSpec { Target = new DispatchTarget() },
        Runtime = new JobRuntimeSpec()
    };

    private static void Apply(ScheduledJobSpec spec, HeartbeatUpdate update)
    {
        if (update.Enabled is { } enabled)
            spec.Intent = enabled ? ScheduleIntent.Active : ScheduleIntent.Disabled;

        if (update.Prompt is not null)
            spec.Prompt = update.Prompt;

        if (update.Every is not null)
        {
            spec.Schedule.Type = ScheduleTypes.Interval;
            spec.Schedule.Every = update.Every.Trim();
        }

        if (update.Windows is not null)
            spec.Schedule.Windows = update.Windows.Count == 0 ? null : update.Windows.ToList();

        if (update.Timezone is not null)
            spec.Schedule.Timezone = update.Timezone.Trim();

        if (update.SessionId is not null)
            spec.Dispatch.Target.SessionId = string.IsNullOrWhiteSpace(update.SessionId)
                ? null
                : update.SessionId.Trim();

        if (update.Conversation is not null)
            spec.Dispatch.Target.Conversation = update.Conversation.IsComplete
                ? update.Conversation
                : null;

        if (update.Agent is not null)
            spec.Agent = string.IsNullOrWhiteSpace(update.Agent) ? null : update.Agent.Trim();

        if (update.Model is not null)
            spec.Model = string.IsNullOrWhiteSpace(update.Model) ? null : update.Model.Trim();

        if (update.TimeoutSeconds is { } timeout)
            spec.Runtime.TimeoutSeconds = timeout;

        // 心跳不走 cron 表达式与一次性时间：强制回到 interval。
        spec.Schedule.Type = ScheduleTypes.Interval;
        spec.Schedule.Cron = null;
        spec.Schedule.RunAt = null;
        spec.TaskType = ScheduleTaskTypes.Agent;
    }

    private static void Validate(ScheduledJobSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Prompt))
            throw new ArgumentException("心跳 Prompt 不能为空。", nameof(spec));

        if (string.IsNullOrWhiteSpace(spec.Schedule.Every))
            throw new ArgumentException("心跳间隔不能为空，例如 6h / 30m。", nameof(spec));

        try
        {
            ScheduleExpressionParser.ParseInterval(spec.Schedule.Every);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"心跳间隔格式无效：{ex.Message}", nameof(spec), ex);
        }

        if (!ScheduleWindowsValidator.ValidateForScheduleType(
                ScheduleTypes.Interval, spec.Schedule.Windows, out var windowsError))
        {
            throw new ArgumentException(windowsError ?? "生效时段无效。", nameof(spec));
        }

        var hasSession = !string.IsNullOrWhiteSpace(spec.Dispatch?.Target?.SessionId);
        var hasConversation = spec.Dispatch?.Target?.Conversation is { IsComplete: true };
        if (!hasSession && !hasConversation)
            throw new ArgumentException("心跳需要一个目标会话或出站 conversation。", nameof(spec));
    }
}
```

- [ ] **Step 5: 注册 DI**

`src/capabilities/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs`，在两个 `AddSeeingScheduler` 重载里 `services.AddSingleton<IScheduleStatusQuery>(...)` 之后各加：

```csharp
        services.AddSingleton<IHeartbeatService, HeartbeatService>();
```

并在文件顶部 `using Seeing.Agent.Scheduler.Abstractions;`（若已有则跳过）。

- [ ] **Step 6: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~HeartbeatServiceTests"
```
Expected: 6 个用例绿。

- [ ] **Step 7: 提交**

```powershell
git add src/capabilities/Seeing.Agent.Scheduler/Abstractions/SchedulerAbstractions.cs src/capabilities/Seeing.Agent.Scheduler/Management/HeartbeatService.cs src/capabilities/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/HeartbeatServiceTests.cs
git commit -m "feat(scheduler): 新增 IHeartbeatService 作为心跳唯一上层入口"
```

---

### Task 8: `EnsureSessionAsync` 的 `origin`

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/ISessionManager.cs`
- Modify: `src/primitives/Seeing.Session/Management/SessionManager.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/ScheduleConversationResolver.cs`
- Modify: `tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs`（`FakeSessionManager`）
- Test: `tests/primitives/Seeing.Session.Tests/Core/EnsureSessionOriginTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `ScheduleRunProfile.OriginWhenCreated`。
- Produces: `Task<SessionData> EnsureSessionAsync(string id, string? selectedAgent = null, string? partitionId = null, string? scenario = null, SessionOrigin? origin = null)`；新建会话时写入 `origin`，已存在会话不改。

- [ ] **Step 1: 写失败测试**

`tests/primitives/Seeing.Session.Tests/Core/EnsureSessionOriginTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Session.Tests.Core;

public class EnsureSessionOriginTests
{
    [Fact]
    public async Task EnsureSession_NewSession_AppliesOrigin()
    {
        var manager = new SessionManager();

        var session = await manager.EnsureSessionAsync("hb_1", origin: SessionOrigin.Heartbeat);

        session.Origin.Should().Be(SessionOrigin.Heartbeat);
    }

    [Fact]
    public async Task EnsureSession_ExistingSession_KeepsOriginalOrigin()
    {
        var manager = new SessionManager();
        await manager.EnsureSessionAsync("u_1", origin: SessionOrigin.User);

        var again = await manager.EnsureSessionAsync("u_1", origin: SessionOrigin.Heartbeat);

        again.Origin.Should().Be(SessionOrigin.User, "已有会话的来源不应被后续调用改写");
    }

    [Fact]
    public async Task EnsureSession_WithoutOrigin_DefaultsToUser()
    {
        var manager = new SessionManager();

        var session = await manager.EnsureSessionAsync("plain");

        session.Origin.Should().Be(SessionOrigin.User);
    }

    [Fact]
    public async Task EnsureSession_ConcurrentFirstCreation_KeepsFirstOrigin()
    {
        var manager = new SessionManager();

        var tasks = new[]
        {
            manager.EnsureSessionAsync("c_1", origin: SessionOrigin.Scheduler),
            manager.EnsureSessionAsync("c_1", origin: SessionOrigin.Heartbeat)
        };
        await Task.WhenAll(tasks);

        (await manager.EnsureSessionAsync("c_1")).Origin.Should().Be(SessionOrigin.Scheduler);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/primitives/Seeing.Session.Tests
```
Expected: 编译失败（`origin` 参数不存在）。

- [ ] **Step 3: 加参数并写入**

`src/primitives/Seeing.Session/Core/ISessionManager.cs`：

```csharp
        /// <summary>确保会话存在（缓存 → 存储 → 创建）</summary>
        /// <param name="scenario">会话级场景名（仅新建时生效；应物化进程默认工作模式）</param>
        /// <param name="origin">
        /// 会话来源（仅本次新建时生效）。已有会话保持原来源，不覆盖。
        /// 平台内部入口（定时任务 / 心跳 / 复盘）必须显式传入，否则会话会被当成用户会话参与任务视图与自动分析判定。
        /// </param>
        Task<SessionData> EnsureSessionAsync(
            string id,
            string? selectedAgent = null,
            string? partitionId = null,
            string? scenario = null,
            SessionOrigin? origin = null);
```

`src/primitives/Seeing.Session/Management/SessionManager.cs`：

`EnsureSessionAsync` 签名同上，把 `origin` 透传给 `EnsureSessionCoreAsync`（含 `_ensureInFlight` 的 `Lazy` 闭包与 `EnsureSessionCoreAsync` 的参数）。`EnsureSessionCoreAsync` 的 `new SessionData { ... }` 里加：

```csharp
                Origin = origin ?? SessionOrigin.User,
```

- [ ] **Step 4: 解析器按画像传 origin**

`src/capabilities/Seeing.Agent.Scheduler/Jobs/ScheduleConversationResolver.cs`，把 Task 2 里留下的调用改成：

```csharp
        await sessionManager.EnsureSessionAsync(
            newSessionId,
            origin: profile.OriginWhenCreated).ConfigureAwait(false);
```

- [ ] **Step 5: 修 `FakeSessionManager`**

`tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs` 的 `EnsureSessionAsync` 补参数：

```csharp
    public Task<SessionData> EnsureSessionAsync(
        string id,
        string? selectedAgent = null,
        string? partitionId = null,
        string? scenario = null,
        SessionOrigin? origin = null) =>
        Task.FromResult(_sessions.TryGetValue(id, out var session)
            ? session
            : Create(partitionId, selectedAgent, scenario));
```

- [ ] **Step 6: 跑测试确认通过**

```powershell
dotnet build tests/primitives/Seeing.Session.Tests
dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~EnsureSessionOriginTests"
dotnet build
```
Expected: 4 个用例绿，全解决方案编译通过。

- [ ] **Step 7: 提交**

```powershell
git add src/primitives/Seeing.Session src/capabilities/Seeing.Agent.Scheduler/Jobs/ScheduleConversationResolver.cs tests/primitives/Seeing.Session.Tests/Core/EnsureSessionOriginTests.cs tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs
git commit -m "feat(session): EnsureSessionAsync 支持创建时写入会话来源"
```

---

### Task 9: 三个心跳工具、技能与命令

**Files:**
- Create: `src/capabilities/Seeing.Agent.Scheduler/Tools/HeartbeatGetTool.cs`
- Create: `src/capabilities/Seeing.Agent.Scheduler/Tools/HeartbeatUpdateTool.cs`
- Create: `src/capabilities/Seeing.Agent.Scheduler/Tools/HeartbeatRunTool.cs`
- Create: `src/capabilities/Seeing.Agent.Scheduler/Skills/heartbeat-management/SKILL.md`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/SchedulerModule.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Commands/SchedulerCommands.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Tools/ScheduleTextFormatter.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/Tools/HeartbeatToolsTests.cs`

**Interfaces:**
- Consumes: `IHeartbeatService`、`HeartbeatSnapshot`、`HeartbeatUpdate`、`ScheduleTextFormatter.DescribeJob`。
- Produces: 工具 id `heartbeat_get` / `heartbeat_update` / `heartbeat_run`；`ScheduleTextFormatter.DescribeHeartbeatLastRun(JobExecutionRecord?)`。

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.Scheduler.Tests/Tools/HeartbeatToolsTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Tools;

public class HeartbeatToolsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static HeartbeatSnapshot Snapshot() => new(
        new ScheduledJobSpec
        {
            Id = SchedulerConstants.HeartbeatJobId,
            Name = "心跳",
            Intent = ScheduleIntent.Active,
            TaskType = ScheduleTaskTypes.Agent,
            Prompt = "检查待办",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h", Timezone = "Asia/Shanghai" },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
        },
        new JobStatus
        {
            JobId = SchedulerConstants.HeartbeatJobId,
            State = JobState.Scheduled,
            NextFireTime = new DateTime(2026, 11, 7, 18, 0, 0)
        },
        new JobExecutionRecord
        {
            JobId = SchedulerConstants.HeartbeatJobId,
            Status = "success",
            StartedAt = new DateTime(2026, 11, 7, 12, 0, 0),
            CompletedAt = new DateTime(2026, 11, 7, 12, 0, 7),
            Source = ScheduleSources.Heartbeat
        },
        SchedulerStarted: true);

    [Fact]
    public async Task Get_ShouldDescribeConfigNextFireAndLastRun()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());

        var tool = new HeartbeatGetTool(NullLogger<HeartbeatGetTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("6h");
        result.Output.Should().Contain("2026-11-07 18:00:00");
        result.Output.Should().Contain("成功");
        result.Output.Should().Contain("检查待办");
    }

    [Fact]
    public async Task Get_WhenNotConfigured_SaysSo()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeartbeatSnapshot(null, null, null, true));

        var tool = new HeartbeatGetTool(NullLogger<HeartbeatGetTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("未配置");
    }

    [Fact]
    public async Task Update_ShouldOnlyForwardProvidedFields()
    {
        HeartbeatUpdate? captured = null;
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.UpdateAsync(It.IsAny<HeartbeatUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<HeartbeatUpdate, CancellationToken>((u, _) => captured = u)
            .ReturnsAsync(Snapshot());

        var tool = new HeartbeatUpdateTool(NullLogger<HeartbeatUpdateTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(
            Args(new { enabled = false }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue(result.Error);
        captured.Should().NotBeNull();
        captured!.Enabled.Should().BeFalse();
        captured.Prompt.Should().BeNull();
        captured.Every.Should().BeNull();
        captured.Agent.Should().BeNull();
    }

    [Fact]
    public async Task Update_WhenValidationFails_ReturnsFailure()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.UpdateAsync(It.IsAny<HeartbeatUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("心跳间隔格式无效"));

        var tool = new HeartbeatUpdateTool(NullLogger<HeartbeatUpdateTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(
            Args(new { every = "6 hours" }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("心跳间隔格式无效");
    }

    [Fact]
    public async Task Run_ShouldMapEveryTriggerResultToDistinctCopy()
    {
        var service = new Mock<IHeartbeatService>();

        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Accepted("run-1"));
        var accepted = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.NotFound());
        var notFound = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Disabled());
        var disabled = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Conflict("任务正在执行中"));
        var conflict = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        accepted.Success.Should().BeTrue();
        accepted.Output.Should().Contain("run-1");
        notFound.Success.Should().BeFalse();
        notFound.Error.Should().Contain("未配置");
        disabled.Success.Should().BeFalse();
        disabled.Error.Should().Contain("未启用");
        conflict.Success.Should().BeFalse();
        conflict.Error.Should().Contain("正在执行");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
```
Expected: 编译失败（三个工具类不存在）。

- [ ] **Step 3: 补文本呈现**

`src/capabilities/Seeing.Agent.Scheduler/Tools/ScheduleTextFormatter.cs` 追加：

```csharp
    /// <summary>最近一次心跳执行的单行摘要；从未执行时返回固定说明。</summary>
    public static string DescribeHeartbeatLastRun(JobExecutionRecord? last)
    {
        if (last is null)
            return "从未执行";

        var status = last.Status switch
        {
            "success" => "成功",
            "failed" => "失败",
            _ => last.Status
        };

        var duration = last.Duration is { } d
            ? $"（耗时 {(int)d.TotalSeconds}s）"
            : string.Empty;

        var error = string.IsNullOrWhiteSpace(last.Error) ? string.Empty : $"；错误: {last.Error}";
        return $"{last.StartedAt:yyyy-MM-dd HH:mm:ss} {status}{duration}{error}";
    }
```

- [ ] **Step 4: 三个工具**

`src/capabilities/Seeing.Agent.Scheduler/Tools/HeartbeatGetTool.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>读取心跳配置、下次触发时间与最近一次执行结果。</summary>
public sealed class HeartbeatGetTool : ToolBase
{
    private readonly IHeartbeatService _service;

    public HeartbeatGetTool(ILogger<HeartbeatGetTool> logger, IHeartbeatService service) : base(logger)
    {
        _service = service;
    }

    public override string Id => "heartbeat_get";

    public override string Description =>
        "读取心跳的当前配置、是否启用、下次触发时间与最近一次执行结果；无需参数。\n\n" +
        "- 心跳是独立的周期任务，不会出现在 cron_list 的结果里。\n" +
        "- 未配置时返回「未配置」，此时用 heartbeat_update 建立配置。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var snapshot = await _service.GetAsync(context.CancellationToken).ConfigureAwait(false);
            if (!snapshot.Configured)
                return Success("心跳未配置", "心跳尚未配置。用 heartbeat_update 设置 prompt、every 与目标会话后才会开始周期触发。");

            var spec = snapshot.Spec!;
            var sb = new StringBuilder();
            sb.AppendLine(ScheduleTextFormatter.DescribeJob(spec, snapshot.Status, schedulerStarted: snapshot.SchedulerStarted));
            sb.AppendLine($"  启用: {(snapshot.Enabled ? "是" : "否")}");
            sb.AppendLine($"  调度器: {(snapshot.SchedulerStarted ? "已启动" : "未启动（不会自动触发）")}");
            sb.AppendLine($"  上次执行: {ScheduleTextFormatter.DescribeHeartbeatLastRun(snapshot.LastRun)}");

            return Success("心跳状态", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
```

`src/capabilities/Seeing.Agent.Scheduler/Tools/HeartbeatUpdateTool.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>局部更新心跳配置：未提供的字段保持不变。</summary>
public sealed class HeartbeatUpdateTool : ToolBase
{
    private readonly IHeartbeatService _service;

    public HeartbeatUpdateTool(ILogger<HeartbeatUpdateTool> logger, IHeartbeatService service) : base(logger)
    {
        _service = service;
    }

    public override string Id => "heartbeat_update";

    public override string Description =>
        "局部更新心跳配置；只改传入的字段，未传的字段保持原值。首次调用会创建心跳配置。\n\n" +
        "- enabled：开关心跳周期触发。\n" +
        "- prompt：每次心跳交给 Agent 的指令（启用时必填）。\n" +
        "- every：间隔，如 6h / 30m；仅支持 interval 语义，不接受 cron 表达式。\n" +
        "- windows：生效时段（可选），留空表示全天；允许跨午夜、禁止交叠。\n" +
        "- session_id / conversation：目标会话或出站对话，两者至少一项。\n" +
        "- agent / model / timeout_seconds：执行身份与超时。\n" +
        "- 返回更新后的完整状态；校验失败会说明原因，配置不变。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            enabled = new { type = "boolean", description = "是否启用周期触发" },
            prompt = new { type = "string", description = "心跳指令（交给 Agent 的任务文本）" },
            every = new { type = "string", description = "间隔，如 6h、30m" },
            timezone = new { type = "string", description = "时区 ID，默认本地" },
            windows = new
            {
                type = "array",
                description = "可选生效时段；空数组表示恢复全天。start 缺省 00:00，end 缺省 23:59:59。",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        start = new { type = "string", description = "HH:mm 或 HH:mm:ss" },
                        end = new { type = "string", description = "HH:mm 或 HH:mm:ss" }
                    }
                }
            },
            session_id = new { type = "string", description = "目标会话 ID" },
            conversation = new
            {
                type = "object",
                description = "可选出站目标；channelId/kind/externalId 需齐备",
                properties = new
                {
                    channelId = new { type = "string", description = "通道 ID（如 qq / wecom）" },
                    kind = new { type = "string", description = "direct / group / guild" },
                    externalId = new { type = "string", description = "通道侧会话 ID" },
                    title = new { type = "string", description = "可选显示名" }
                },
                required = new[] { "channelId", "kind", "externalId" }
            },
            agent = new { type = "string", description = "Agent ID，留空沿用默认 Agent" },
            model = new { type = "string", description = "模型 ID" },
            timeout_seconds = new { type = "integer", description = "单次执行超时秒数" }
        }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var update = new HeartbeatUpdate
        {
            Enabled = GetBoolArgument(arguments, "enabled"),
            Prompt = GetStringArgument(arguments, "prompt"),
            Every = GetStringArgument(arguments, "every"),
            Timezone = GetStringArgument(arguments, "timezone"),
            SessionId = GetStringArgument(arguments, "session_id"),
            Agent = GetStringArgument(arguments, "agent"),
            Model = GetStringArgument(arguments, "model"),
            TimeoutSeconds = GetIntArgument(arguments, "timeout_seconds"),
            Windows = ReadWindows(arguments),
            Conversation = ReadConversation(arguments)
        };

        try
        {
            var snapshot = await _service.UpdateAsync(update, context.CancellationToken)
                .ConfigureAwait(false);
            var spec = snapshot.Spec!;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(ScheduleTextFormatter.DescribeJob(spec, snapshot.Status, schedulerStarted: snapshot.SchedulerStarted));
            sb.AppendLine($"  上次执行: {ScheduleTextFormatter.DescribeHeartbeatLastRun(snapshot.LastRun)}");
            return Success("心跳配置已更新", sb.ToString().TrimEnd());
        }
        catch (ArgumentException ex)
        {
            return Failure($"心跳配置无效: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    private static IReadOnlyList<ScheduleWindow>? ReadWindows(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("windows", out var element)
            || element.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<ScheduleWindow>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(new ScheduleWindow
            {
                Start = item.TryGetProperty("start", out var s) ? s.GetString() : null,
                End = item.TryGetProperty("end", out var e) ? e.GetString() : null
            });
        }

        return list;
    }

    private static ConversationSpec? ReadConversation(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("conversation", out var element)
            || element.ValueKind != JsonValueKind.Object)
            return null;

        var spec = new ConversationSpec
        {
            ChannelId = element.TryGetProperty("channelId", out var c) ? c.GetString() : null,
            Kind = element.TryGetProperty("kind", out var k) ? k.GetString() : null,
            ExternalId = element.TryGetProperty("externalId", out var x) ? x.GetString() : null,
            Title = element.TryGetProperty("title", out var t) ? t.GetString() : null
        };

        return spec.IsComplete ? spec : null;
    }
}
```

`src/capabilities/Seeing.Agent.Scheduler/Tools/HeartbeatRunTool.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>立即执行一次心跳。</summary>
public sealed class HeartbeatRunTool : ToolBase
{
    private readonly IHeartbeatService _service;

    public HeartbeatRunTool(ILogger<HeartbeatRunTool> logger, IHeartbeatService service) : base(logger)
    {
        _service = service;
    }

    public override string Id => "heartbeat_run";

    public override string Description =>
        "立即执行一次心跳，不影响后续周期调度；无需参数。\n\n" +
        "- 未配置、未启用、调度器未启动或正在执行时会说明具体原因，不会假装已触发。\n" +
        "- 结果会写回目标会话并进入执行历史。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var result = await _service.RunNowAsync(context.CancellationToken).ConfigureAwait(false);
            return result switch
            {
                TriggerResult.Accepted a => Success(
                    "心跳已触发",
                    $"RunId: {a.RunId}\n结果会在执行结束后写入目标会话。"),
                TriggerResult.NotFound => Failure("心跳未配置，请先用 heartbeat_update 设置。"),
                TriggerResult.Disabled => Failure("心跳未启用或调度器未启动，无法立即执行。"),
                TriggerResult.Conflict c => Failure($"心跳正在执行中: {c.Reason}"),
                _ => Failure("未知触发结果。")
            };
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
```

- [ ] **Step 5: 技能**

`src/capabilities/Seeing.Agent.Scheduler/Skills/heartbeat-management/SKILL.md`：

```markdown
---
name: heartbeat-management
description: Read and adjust the periodic heartbeat (interval agent run) — separate from cron jobs.
---

# Heartbeat

心跳是一个独立的周期任务，配置与执行历史都不在定时任务里。

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

用户说「定期检查一下」「每天提醒我」「每几小时看一眼」但**不需要固定钟点**时用心跳；
需要固定钟点（每天 9 点）时用 `cron_create`。
```

- [ ] **Step 6: 模块挂载与命令**

`src/capabilities/Seeing.Agent.Scheduler/SchedulerModule.cs`：

`s_providedTools` 追加：

```csharp
        "heartbeat_get",
        "heartbeat_update",
        "heartbeat_run",
```

`ActivateAsync` 里 `CronRunTool` 之后追加：

```csharp
        if (services.GetService<HeartbeatGetTool>() is { } hbGet)
            await tm.RegisterToolAsync(hbGet, cancellationToken).ConfigureAwait(false);
        if (services.GetService<HeartbeatUpdateTool>() is { } hbUpdate)
            await tm.RegisterToolAsync(hbUpdate, cancellationToken).ConfigureAwait(false);
        if (services.GetService<HeartbeatRunTool>() is { } hbRun)
            await tm.RegisterToolAsync(hbRun, cancellationToken).ConfigureAwait(false);
```

`src/capabilities/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs` 的 `AddSeeingSchedulerTools` 追加：

```csharp
        services.AddSingleton<HeartbeatGetTool>();
        services.AddSingleton<HeartbeatUpdateTool>();
        services.AddSingleton<HeartbeatRunTool>();
```

`src/capabilities/Seeing.Agent.Scheduler/Commands/SchedulerCommands.cs`：`HeartbeatRunCommand` 改依赖 `IHeartbeatService`：

```csharp
internal sealed class HeartbeatRunCommand : ICommand
{
    private readonly IHeartbeatService _heartbeat;

    public HeartbeatRunCommand(IHeartbeatService heartbeat) => _heartbeat = heartbeat;

    public CommandMetadata Metadata => CommandMetadata.Simple(
        "heartbeat-run",
        "立即执行一次心跳",
        "/heartbeat-run",
        CommandCategory.System);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var result = await _heartbeat.RunNowAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            TriggerResult.Accepted a => CommandResult.Ok($"Heartbeat triggered with RunId: {a.RunId}"),
            TriggerResult.NotFound => CommandResult.Fail("Heartbeat is not configured."),
            TriggerResult.Disabled => CommandResult.Fail("Heartbeat is disabled or scheduler is not started."),
            TriggerResult.Conflict c => CommandResult.Fail(c.Reason),
            _ => CommandResult.Fail("Unknown result.")
        };
    }
}
```

`SchedulerModule.RegisterCommands` 里 `new HeartbeatRunCommand(_manager)` 改为：

```csharp
            new HeartbeatRunCommand(services.GetRequiredService<IHeartbeatService>())
```

- [ ] **Step 7: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~HeartbeatToolsTests|FullyQualifiedName~SchedulerToolRegistrationTests|FullyQualifiedName~SchedulerCommandRegistrationTests|FullyQualifiedName~SchedulerSkillRegistrationTests"
```
Expected: 全绿。若 `SchedulerToolRegistrationTests` 断言了工具总数，按实际数量更新。

- [ ] **Step 8: 提交**

```powershell
git add src/capabilities/Seeing.Agent.Scheduler tests/capabilities/Seeing.Agent.Scheduler.Tests
git commit -m "feat(scheduler): 新增 heartbeat_get/update/run 工具与 heartbeat-management 技能"
```

---

### Task 10: `/heartbeat` 页面与相邻 UI 修正

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/HeartbeatPage.razor`（整体重写）
- Modify: `samples/Seeing.Agent.WebUI/Pages/CronJobsPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/Scheduler/SchedulerSettingsCard.razor`
- Test: 手动验证（Blazor 页面，无自动化 UI 测试）

**Interfaces:**
- Consumes: `IHeartbeatService`（`GetAsync` / `UpdateAsync` / `RunNowAsync` / `CancelAsync` / `GetHistoryAsync`）、`HeartbeatSnapshot`、`HeartbeatUpdate`、`JobStatus`、`JobExecutionRecord`。

- [ ] **Step 1: 重写 `HeartbeatPage.razor`**

整体替换文件内容：

```razor
@using AntDesign
@using Seeing.Agent.Abstractions.Scheduling
@using Seeing.Agent.Scheduler
@using Seeing.Agent.Scheduler.Abstractions
@using Seeing.Agent.Scheduler.Engine
@using Seeing.Agent.Scheduler.Models
@using Seeing.Agent.WebUI.Components.CronJobs
@using Seeing.Agent.WebUI.Services
@inject IHeartbeatService HeartbeatService
@inject IMessageService MessageService
@implements IDisposable

<PageTitle>心跳 - Seeing</PageTitle>

<WorkspacePageShell Loading="@_loading">
    <Breadcrumb>
        <BreadcrumbItem>控制中心</BreadcrumbItem>
        <BreadcrumbItem>心跳</BreadcrumbItem>
    </Breadcrumb>
    <Extra>
        <Space>
            <Button Icon="@IconType.Outline.Reload" OnClick="() => LoadAsync(true)">刷新</Button>
            @if (_snapshot?.Enabled == true && _snapshot.SchedulerStarted)
            {
                <Button Icon="@IconType.Outline.Thunderbolt"
                        Loading="@_running"
                        Disabled="@(_snapshot.Status?.State == JobState.Running)"
                        OnClick="RunNowAsync">立即执行</Button>
            }
            <Button Type="@ButtonType.Primary" Icon="@IconType.Outline.Save" Loading="@_saving" OnClick="SaveAsync">
                保存配置
            </Button>
        </Space>
    </Extra>
    <ChildContent>
        @if (!_snapshot!.SchedulerStarted)
        {
            <Alert Type="@AlertType.Warning" ShowIcon="true" Style="margin-bottom: 16px;"
                   Message="调度器未启动，心跳不会自动触发"
                   Description="心跳随调度器一起工作。请到「设置 → 调度器」启动调度器后再回来。" />
        }

        <Card Title="状态" Style="margin-bottom: 16px;">
            <Descriptions Column="4">
                <DescriptionsItem Title="配置">
                    @(_snapshot.Configured ? "已配置" : "未配置")
                </DescriptionsItem>
                <DescriptionsItem Title="启用">
                    @if (_snapshot.Configured)
                    {
                        <Switch Checked="@_form.Enabled" CheckedChanged="@OnEnabledChanged" />
                    }
                    else
                    {
                        <Tag>未配置</Tag>
                    }
                </DescriptionsItem>
                <DescriptionsItem Title="下次触发">
                    @if (_snapshot.Status?.NextFireTime is { } next)
                    {
                        <JobNextRunTimer NextRun="@next" State="@(_snapshot.Status.State)" Expired="false" />
                    }
                    else
                    {
                        <span>-</span>
                    }
                </DescriptionsItem>
                <DescriptionsItem Title="上次执行">
                    <div style="line-height: 1.6;">
                        @if (_snapshot.LastRun is { } last)
                        {
                            <Tag Color="@(last.Status == "success" ? "green" : "red")">
                                @(last.Status == "success" ? "成功" : "失败")
                            </Tag>
                            <span>@last.StartedAt.ToString("MM-dd HH:mm:ss")</span>
                            @if (last.Duration is { } d)
                            {
                                <span style="color:#888;">（@{((int)d.TotalSeconds)}s）</span>
                            }
                            @if (!string.IsNullOrWhiteSpace(last.Error))
                            {
                                <div style="color:#cf1322; font-size:12px;">@last.Error</div>
                            }
                        }
                        else
                        {
                            <span>从未执行</span>
                        }
                    </div>
                </DescriptionsItem>
            </Descriptions>
            @if (_snapshot.Status?.State == JobState.Running)
            {
                <Button Danger Size="@ButtonSize.Small" Loading="@_cancelling" OnClick="CancelAsync">取消正在执行的心跳</Button>
            }
        </Card>

        <Card Title="配置" Style="margin-bottom: 16px;">
            <Form Layout="@FormLayout.Vertical" Model="@_form" Context="hbForm">
                <Row Gutter="16">
                    <Col Span="8">
                        <FormItem Label="执行间隔" Required>
                            <Input @bind-Value="@_form.Every" Placeholder="6h、30m" />
                            <small style="color:#999;">数字 + 单位（s/m/h/d）。心跳只支持间隔，不支持固定钟点。</small>
                        </FormItem>
                    </Col>
                    <Col Span="8">
                        <FormItem Label="时区">
                            <Input @bind-Value="@_form.Timezone" Placeholder="Asia/Shanghai" />
                        </FormItem>
                    </Col>
                    <Col Span="8">
                        <FormItem Label="超时（秒）">
                            <InputNumber @bind-Value="@_form.TimeoutSeconds" Min="10" Max="3600" Style="width:100%;" />
                        </FormItem>
                    </Col>
                </Row>

                <FormItem Label="生效时段（可选）">
                    <small style="color:#999; display:block; margin-bottom:8px;">
                        留空表示全天。时间均为上方时区的本地墙钟；开始默认 00:00，结束默认 23:59:59；允许跨午夜；不可交叠。
                    </small>
                    @for (var i = 0; i < _windows.Count; i++)
                    {
                        var index = i;
                        <div style="display:flex; gap:8px; margin-bottom:8px;">
                            <Input @bind-Value="@_windows[index].Start" Placeholder="开始 HH:mm" Style="flex:1;" />
                            <Input @bind-Value="@_windows[index].End" Placeholder="结束 HH:mm:ss" Style="flex:1;" />
                            <Button Danger OnClick="() => RemoveWindow(index)">删除</Button>
                        </div>
                    }
                    <Button Type="@ButtonType.Dashed" Block OnClick="AddWindow">添加时段</Button>
                </FormItem>

                <Row Gutter="16">
                    <Col Span="12">
                        <FormItem Label="目标会话">
                            <Input @bind-Value="@_form.SessionId" Placeholder="如 main" />
                            <small style="color:#999;">心跳结果写入该会话（只留痕，不唤醒）。</small>
                        </FormItem>
                    </Col>
                    <Col Span="12">
                        <FormItem Label="出站对话（可选）">
                            <ConversationPicker Placeholder="选择要推送的对话"
                                                OnSelected="OnConversationSelected" />
                            @if (_form.Conversation is { IsComplete: true } c)
                            {
                                <div style="margin-top:8px; font-size:12px;">
                                    <Tag Color="blue">@c.ChannelId/@c.Kind/@c.ExternalId</Tag>
                                    <a style="cursor:pointer;" @onclick="ClearConversation">清除</a>
                                </div>
                            }
                        </FormItem>
                    </Col>
                </Row>

                <Row Gutter="16">
                    <Col Span="8">
                        <FormItem Label="Agent">
                            <Input @bind-Value="@_form.Agent" Placeholder="留空使用默认 Agent" />
                        </FormItem>
                    </Col>
                    <Col Span="8">
                        <FormItem Label="模型">
                            <Input @bind-Value="@_form.Model" Placeholder="留空使用 Agent 默认模型" />
                        </FormItem>
                    </Col>
                </Row>

                <FormItem Label="心跳 Prompt" Required>
                    <TextArea @bind-Value="@_form.Prompt" Rows="6" AutoSize="true"
                              Placeholder="每次心跳交给 Agent 的指令" />
                    <small style="color:#999;">
                        必填。建议写明「检查什么、什么情况下汇报、没事怎么回复」。
                        <a style="cursor:pointer; margin-left:6px;" @onclick="FillExample">填入示例</a>
                    </small>
                </FormItem>

                @if (!string.IsNullOrWhiteSpace(_error))
                {
                    <Alert Type="@AlertType.Error" ShowIcon="true" Message="@_error" />
                }
            </Form>
        </Card>

        <Card Title="执行历史">
            @if (_history.Count == 0)
            {
                <div style="padding: 24px; text-align:center; color:#999;">
                    还没有执行记录。@(_snapshot.Configured ? "保存配置后等待下一次触发，或点「立即执行」。" : "先保存配置。")
                </div>
            }
            else
            {
                <Table TItem="JobExecutionRecord" DataSource="@_history" RowKey="@(r => r.RunId)">
                    <PropertyColumn Property="r => r.StartedAt" Title="执行时间">
                        @context.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")
                    </PropertyColumn>
                    <PropertyColumn Property="r => r.Source" Title="来源">
                        <Tag Color="@(context.Source == ScheduleSources.Heartbeat ? "orange" : "blue")">@context.Source</Tag>
                    </PropertyColumn>
                    <PropertyColumn Property="r => r.Status" Title="状态">
                        <Tag Color="@(context.Status == "success" ? "green" : "red")">
                            @(context.Status == "success" ? "成功" : "失败")
                        </Tag>
                    </PropertyColumn>
                    <PropertyColumn Property="r => r.Duration" Title="耗时">
                        @(context.Duration is { } d ? $"@{((int)d.TotalSeconds)}s" : "-")
                    </PropertyColumn>
                    <PropertyColumn Property="r => r.Output" Title="内容">
                        @Truncate(context.Output ?? context.Error, 60)
                    </PropertyColumn>
                    <ActionColumn Title="操作">
                        <Button Size="@ButtonSize.Small" OnClick="() => ShowDetail(context)">查看详情</Button>
                    </ActionColumn>
                </Table>

                @if (_hasMore)
                {
                    <div style="text-align:center; margin-top:16px;">
                        <Button Loading="@_loadingMore" OnClick="LoadMoreAsync">加载更多</Button>
                    </div>
                }
            }
        </Card>
    </ChildContent>
</WorkspacePageShell>

<ExecutionResultModal Visible="@_detailVisible"
                      VisibleChanged="@(v => _detailVisible = v)"
                      Record="@_selectedRecord" />

@code {
    private const int PageSize = 20;

    private HeartbeatSnapshot? _snapshot;
    private FormModel _form = new();
    private readonly List<WindowRow> _windows = new();
    private readonly List<JobExecutionRecord> _history = new();
    private JobExecutionRecord? _selectedRecord;
    private bool _detailVisible;
    private bool _loading;
    private bool _saving;
    private bool _running;
    private bool _cancelling;
    private bool _loadingMore;
    private bool _hasMore;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync(true);
    }

    /// <summary>加载快照与历史。快照是深拷贝，表单改它不会写回配置缓存。</summary>
    private async Task LoadAsync(bool showLoading = false)
    {
        if (showLoading)
            _loading = true;

        try
        {
            _snapshot = await HeartbeatService.GetAsync();
            ApplySnapshotToForm(_snapshot);

            _history.Clear();
            var page = await HeartbeatService.GetHistoryAsync(PageSize, 0);
            _history.AddRange(page);
            _hasMore = page.Count == PageSize;
        }
        catch (Exception ex)
        {
            MessageService.Error($"读取心跳状态失败: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplySnapshotToForm(HeartbeatSnapshot snapshot)
    {
        var spec = snapshot.Spec;
        _form = new FormModel
        {
            Enabled = spec?.Intent == ScheduleIntent.Active,
            Every = spec?.Schedule.Every ?? "6h",
            Timezone = string.IsNullOrWhiteSpace(spec?.Schedule.Timezone)
                ? TimeZoneInfo.Local.Id
                : spec!.Schedule.Timezone,
            SessionId = spec?.Dispatch?.Target?.SessionId ?? "main",
            Conversation = spec?.Dispatch?.Target?.Conversation,
            Agent = spec?.Agent,
            Model = spec?.Model,
            TimeoutSeconds = spec?.Runtime.TimeoutSeconds ?? SchedulerConstants.DefaultTimeoutSeconds,
            Prompt = spec?.Prompt ?? string.Empty
        };

        _windows.Clear();
        foreach (var w in spec?.Schedule.Windows ?? new List<ScheduleWindow>())
            _windows.Add(new WindowRow { Start = w.Start ?? "", End = w.End ?? "" });
    }

    private async Task OnEnabledChanged(bool enabled)
    {
        _form.Enabled = enabled;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        _error = null;

        // 先本地校验，避免把明显非法的值交给服务再弹一次错误
        if (string.IsNullOrWhiteSpace(_form.Prompt))
        {
            _error = "心跳 Prompt 为必填项";
            return;
        }

        _saving = true;
        try
        {
            var update = new HeartbeatUpdate
            {
                Enabled = _form.Enabled,
                Prompt = _form.Prompt,
                Every = _form.Every,
                Timezone = _form.Timezone,
                SessionId = _form.SessionId,
                Conversation = _form.Conversation,
                Agent = _form.Agent,
                Model = _form.Model,
                TimeoutSeconds = _form.TimeoutSeconds,
                Windows = _windows
                    .Where(w => !string.IsNullOrWhiteSpace(w.Start) || !string.IsNullOrWhiteSpace(w.End))
                    .Select(w => new ScheduleWindow
                    {
                        Start = string.IsNullOrWhiteSpace(w.Start) ? null : w.Start.Trim(),
                        End = string.IsNullOrWhiteSpace(w.End) ? null : w.End.Trim()
                    })
                    .ToList()
            };

            await HeartbeatService.UpdateAsync(update);
            MessageService.Success("心跳配置已保存");
            await LoadAsync();
        }
        catch (ArgumentException ex)
        {
            _error = ex.Message;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            MessageService.Error($"保存失败: {ex.Message}");
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task RunNowAsync()
    {
        _running = true;
        try
        {
            var result = await HeartbeatService.RunNowAsync();
            switch (result)
            {
                case TriggerResult.Accepted a:
                    MessageService.Success($"心跳已触发，RunId={a.RunId}");
                    break;
                case TriggerResult.NotFound:
                    MessageService.Warning("心跳未配置，请先保存配置");
                    break;
                case TriggerResult.Disabled:
                    MessageService.Warning("心跳未启用或调度器未启动");
                    break;
                case TriggerResult.Conflict c:
                    MessageService.Warning($"心跳正在执行中: {c.Reason}");
                    break;
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageService.Error($"触发失败: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task CancelAsync()
    {
        _cancelling = true;
        try
        {
            var ok = await HeartbeatService.CancelAsync();
            if (ok)
                MessageService.Success("已请求取消");
            else
                MessageService.Warning("心跳当前不在执行中");
            await LoadAsync();
        }
        finally
        {
            _cancelling = false;
        }
    }

    private async Task LoadMoreAsync()
    {
        _loadingMore = true;
        try
        {
            var page = await HeartbeatService.GetHistoryAsync(PageSize, _history.Count);
            _history.AddRange(page);
            _hasMore = page.Count == PageSize;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void AddWindow() => _windows.Add(new WindowRow());

    private void RemoveWindow(int index)
    {
        if (index >= 0 && index < _windows.Count)
            _windows.RemoveAt(index);
    }

    private void OnConversationSelected(Seeing.Agent.Abstractions.Channels.ConversationInfo conversation)
    {
        _form.Conversation = new ConversationSpec
        {
            ChannelId = conversation.ChannelId,
            Kind = conversation.Kind.ToString().ToLowerInvariant(),
            ExternalId = conversation.ExternalId,
            Title = conversation.Title
        };
    }

    private void ClearConversation() => _form.Conversation = null;

    private void FillExample()
    {
        _form.Prompt =
            """
            检查当前工作区状态：
            1. 是否有未完成的待办
            2. 是否有需要关注的变更或失败
            如果没有需要处理的事，只回复「HEARTBEAT_OK」。
            """;
    }

    private void ShowDetail(JobExecutionRecord record)
    {
        _selectedRecord = record;
        _detailVisible = true;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "-";
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    public void Dispose() { }

    private sealed class FormModel
    {
        public bool Enabled { get; set; }
        public string Every { get; set; } = "6h";
        public string Timezone { get; set; } = TimeZoneInfo.Local.Id;
        public string SessionId { get; set; } = "main";
        public ConversationSpec? Conversation { get; set; }
        public string? Agent { get; set; }
        public string? Model { get; set; }
        public int TimeoutSeconds { get; set; } = SchedulerConstants.DefaultTimeoutSeconds;
        public string Prompt { get; set; } = string.Empty;
    }

    private sealed class WindowRow
    {
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
    }
}
```

- [ ] **Step 2: 定时任务页通知过滤**

`samples/Seeing.Agent.WebUI/Pages/CronJobsPage.razor`，三个通知处理方法开头各加一行守卫（`_jobs` 已在 `LoadAsync` 里填充）：

```csharp
    private void OnJobError(object? sender, JobErrorEventArgs args)
    {
        // 只处理定时任务面的通知；心跳是独立入口，不该在定时任务页弹出
        if (!_jobs.Any(j => j.Id == args.JobId))
            return;

        _ = InvokeAsync(async () =>
        {
```

```csharp
    private void OnJobSuccess(object? sender, JobSuccessEventArgs args)
    {
        if (!_jobs.Any(j => j.Id == args.JobId))
            return;
```

```csharp
    private void OnJobProgress(object? sender, JobProgressEventArgs args)
    {
        if (!_jobs.Any(j => j.Id == args.JobId))
            return;
```

- [ ] **Step 3: 设置卡去掉心跳重载**

`samples/Seeing.Agent.WebUI/Components/Scheduler/SchedulerSettingsCard.razor`：

删除 `ReloadScheduler` 方法与 `OnReload="ReloadScheduler"` 绑定（`SchedulerStatusPanel` 的 `ShowControls="true"` 保留启动/停止）；`SaveAsync` 里删除 `await ScheduleManager.ReloadHeartbeatAsync();`。

在 `<Alert ... Message="调度器使用 Quartz.NET 框架，支持持久化和任务恢复" />` 之后追加：

```razor
<Alert Type="@AlertType.Info" ShowIcon="true"
       Message="并发与持久化设置在进程启动时生效，修改后需重启应用"
       Style="margin-bottom: 16px;" />
```

`@code` 里的 `@inject IScheduleManager ScheduleManager` 若不再使用则删除（`SchedulerStatusService` 已承担启动/停止）。

- [ ] **Step 4: 编译整个解决方案**

```powershell
dotnet build
```
Expected: 编译通过，无 `IHeartbeatService` / `ReloadHeartbeatAsync` 相关错误。

- [ ] **Step 5: 手动跑通页面**

```powershell
dotnet run --project samples/Seeing.Agent.WebUI
```

按顺序确认：

1. 打开 `/heartbeat`，未配置时显示 `配置: 未配置`、`下次触发: -`、`从未执行`，且「立即执行」按钮不出现。
2. 点「填入示例」→ 保存 → 出现 `已配置`，启用开关为开，`下次触发` 是当前时间 + 6h。
3. 到 `/cron-jobs`，任务列表**没有**心跳，任务总数不含它；等一次心跳执行后，定时任务页**不弹**心跳通知。
4. 在 `/heartbeat` 点「立即执行」，历史里出现一条 `来源=heartbeat`、`状态=成功`。
5. 打开目标会话（`main`），只有一条助手消息，**没有**未读的系统提示气泡。
6. 关闭启用开关 → 保存 → `下次触发` 变 `-`，`/cron-jobs` 仍无心跳。

- [ ] **Step 6: 提交**

```powershell
git add samples/Seeing.Agent.WebUI
git commit -m "feat(webui): 重写心跳页为状态/配置/历史三段，定时任务页过滤心跳通知"
```

---

### Task 11: Release notes 与索引

**Files:**
- Create: `docs/architecture/36-heartbeat-rework-release-notes.md`
- Modify: `docs/architecture/02-modules.md`
- Modify: `AGENTS.md`
- Modify: `CLAUDE.md`

**Interfaces:** 无代码接口。

- [ ] **Step 1: 写 Release notes**

`docs/architecture/36-heartbeat-rework-release-notes.md`：

```markdown
# 心跳重构 Release Notes

**日期:** 2026-11-07
**相关模块:** `Seeing.Agent.Scheduler`、`Seeing.Session`、`Seeing.Agent.WebUI`

## 一句话

心跳不再是一条独立的 `HeartbeatJob` + `HeartbeatOptions`，而是调度执行内核上的一个**执行画像**加 `jobs.json` 里的**独立槽位**；上层入口是 `IHeartbeatService`，与定时任务共用内核但不进入对方的集合。

## 架构

```
上层（分开）
  定时任务面  IScheduleManager 用户任务 API / cron_* / /cron-jobs
  心跳面      IHeartbeatService / heartbeat_* / /heartbeat

下层（一套）
  JobsFile（Jobs + Heartbeat 槽位）
  QuartzSchedulerEngine（只注册 AgentJob）
  ScheduleRunProfile（一次执行的身份）
  IScheduledJobDispatcher（按布局写回）
```

## 修复的行为缺陷

| 缺陷 | 现在 |
|---|---|
| 默认目标下心跳输出被丢弃 | 结果始终写回目标会话（助手消息，已消费） |
| 心跳执行历史从不落盘 | `jobs_history/_heartbeat.json` 正常写入 |
| 心跳状态恒为 `Disabled` | `GetJobStatusAsync` 覆盖槽位，返回真实状态 |
| 激活时段跳过 / Hook 拦截后「立即执行」永久冲突 | 每条返回路径都通知监听器，运行锁正常释放 |
| 调度器未启动时「已触发」假成功 | `RunNowAsync` 返回非 `Accepted` 并说明原因 |
| 非法间隔让整个调度器起不来 | 单个任务注册失败只跳过该任务 |
| 页面改输入框即改活配置 | 页面绑定快照深拷贝 |
| 活跃时段解析失败等于不限制 | 改用 `ScheduleSpec.Windows`，与 cron 同一套校验 |
| 心跳会话来源是 `User` | 新建会话写 `SessionOrigin.Heartbeat` |
| 每次启动约 5 秒打一次心跳 | 有 `LastRunAt` 时按「上次 + 间隔」起算，misfire `DoNothing` |
| `last` / `inbox` 目标不可靠 | 删除。目标只有会话与 conversation |
| 定时任务总数含心跳 | 引擎计数排除保留 id |

## 破坏性变更（不做迁移）

- `SchedulerOptions.Heartbeat`、`HeartbeatOptions`、`ActiveHoursOptions`、`HeartbeatTargets` 删除。旧 `seeing.json` 中的 `Scheduler.Heartbeat` 被忽略，下次保存调度器设置时从文件消失；不生成等价的心跳槽位。
- `HeartbeatJob` 删除，`AgentJob` 是唯一 Job 类型。
- `IScheduleManager.ReloadHeartbeatAsync` 删除。
- `RunJobOnceAsync("_heartbeat")` 返回 `NotFound`。立即执行改走 `IHeartbeatService.RunNowAsync`。
- `scheduler.heartbeat.before` / `scheduler.heartbeat.after` 不再触发；改用 `scheduler.job.before_execute` / `after_execute` 并读载荷 `source=heartbeat`。
- `_heartbeat` 是保留 id：`CreateOrReplaceJobAsync` / `DeleteJobAsync` / `SetJobIntentAsync` 抛 `InvalidOperationException`。

## 已知边界

- 心跳写进**已有用户会话**时，该会话 `Origin` 保持 `User`；来源区分靠消息 `Kind=heartbeat`。`SessionOrigin` 只承诺本功能新建的会话。
- 心跳不支持 cron 表达式与一次性时间，只支持间隔 + 生效时段。
- 心跳的生效时段走窗口触发器（`DailyTimeInterval`），窗口内不按 `LastRunAt` 对齐，重启后从下一个窗口边界重新对齐。

## 测试

见 `docs/superpowers/specs/2026-11-07-heartbeat-rework-design.md` §7。关键守卫：槽位排除、保留 id、状态、历史、画像、投递布局、运行锁、未启动、注册隔离、节律、局部更新、来源、计数、通知过滤。
```

- [ ] **Step 2: 更新模块索引**

`docs/architecture/02-modules.md` 的 Scheduler 行改为：

```markdown
| `Seeing.Agent.Scheduler` | `scheduler` | 执行内核（Quartz + `AgentJob` + 执行画像）共用；上层两个入口：定时任务（`IScheduleManager` / `cron_*`）与心跳（`IHeartbeatService` / `heartbeat_*`），互不进入对方集合 |
```

- [ ] **Step 3: 更新 WHERE TO LOOK**

`AGENTS.md` 的 WHERE TO LOOK 表，在「Todo 存取」行之前插入：

```markdown
| 心跳 | 契约 `src/capabilities/Seeing.Agent.Scheduler/Abstractions/SchedulerAbstractions.cs`（`IHeartbeatService`）；实现 `Management/HeartbeatService.cs`；执行画像 `Models/ScheduleRunProfile.cs`；槽位 `Models/SchedulerModels.cs`（`JobsFile.Heartbeat`）；页面 `samples/Seeing.Agent.WebUI/Pages/HeartbeatPage.razor` | 与定时任务共用 `AgentJob`，身份由画像承载；`_heartbeat` 是保留 id，不在 `JobsFile.Jobs` 内；见 `docs/architecture/36` |
```

`CLAUDE.md` 的 Core Concepts 或模块说明中追加一行：

```markdown
- **`IHeartbeatService`**（`Scheduler/Management/HeartbeatService.cs`）— 心跳的唯一外部入口；心跳是 `jobs.json` 里的独立槽位（`JobsFile.Heartbeat`，id 固定 `_heartbeat`），与定时任务共用 `AgentJob` + `ScheduleRunProfile`，但不出现在 `cron_*` 与 `/cron-jobs`。
```

- [ ] **Step 4: 提交**

```powershell
git add docs/architecture AGENTS.md CLAUDE.md
git commit -m "docs(architecture): 心跳重构 release notes 与索引"
```

---

## 自审记录

**Spec 覆盖检查**

| Spec 章节 | 对应 Task |
|---|---|
| §4.1 执行画像 | 1、2 |
| §4.2 投递布局 | 1、3 |
| §4.3 新建会话来源 | 2、8 |
| §4.4 保留 id | 4 |
| §4.5 历史/状态/计数 | 4 |
| §4.6 触发起算 | 5 |
| §5.1 槽位 | 6 |
| §5.2 `IHeartbeatService` | 7 |
| §5.3 工具与技能 | 9 |
| §5.4 页面 | 10 |
| §6 破坏性变更 | 6、11 |
| §7 测试 | 各 Task 内联 |
| §8 实现顺序 | Task 顺序即该顺序 |

**占位符扫描**：无 TBD / TODO；每个代码步骤都有完整实现。

**类型一致性**：`ScheduleRunProfile` 属性名在 Task 1 定义，Task 2/6 使用一致；`HeartbeatSnapshot` / `HeartbeatUpdate` 在 Task 7 定义，Task 9/10 使用一致；`ScheduleManager.SaveHeartbeatAsync` / `HeartbeatSpec` / `RunHeartbeatAsync` / `CancelHeartbeatAsync` 在 Task 6 定义，Task 7/9 使用一致。

**已知取舍**：Task 6 一次性删除旧类型并改所有依赖测试，是这批任务里最大的一次改动。它无法再拆——`HeartbeatOptions` 一删，`RegisterHeartbeatAsync` 就必须改写法，测试夹具也必须同步，否则编译不过。

