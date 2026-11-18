# WebUI 斜杠命令与宿主动作实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 WebUI 的斜杠命令能打开对应交互面板或显示临时结果，并让子会话默认只读、接管后放开编辑。

**Architecture:** 命令通过新增的 `CommandAction` 声明“请求宿主做什么”，`ExecutionRunner` 透传到 `CommandResultEvent`；WebUI 用 circuit 级的 `HostActionRegistry` 执行动作，未注册/不可用时回退显示临时系统条目。子会话编辑能力由 `!isChild || Owner == Human` 决定。

**Tech Stack:** .NET 10、C#、Blazor Server、AntDesign Blazor、xUnit v3、Moq、FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-11-18-webui-command-host-actions-design.md`

## Global Constraints

- 能力包只依赖 `Seeing.Agent.Abstractions`；宿主动作实现只放 `samples/Seeing.Agent.WebUI`。
- 不把命令结果写入会话；临时条目只在当前时间线存在。
- `CommandResultEvent` 保持 record + init 属性形状，不改 positional record。
- 测试禁止固定时长等待；用条件等待或直接断言。
- 新代码注释用中文；标识符用英文。
- 不引入 bUnit；组件级行为用源码契约测试（读取 `.razor` 断言关键接线）。
- 测试运行方式：`dotnet build <测试项目>` 后 `dotnet vstest <dll> --TestCaseFilter:...`（MTP 在当前环境无法发现测试）。

## Review Focus

1. **ACP 透传会话**：`/model xxx` 必须返回 `Unavailable` 并说明原因，不得静默改模型。
2. **未接管子会话**：编辑控件（模型/ACP/Agent/重命名/清空/出站/自动批准/新建/分叉）全部禁用；接管后立即放开，交还后立即收回。
3. **未知/未注册动作**：必须回退显示命令 `Message`，不得静默丢弃。
4. **临时条目生命周期**：`ResetFromSession` 后消失，且不出现在会话消息与 LLM 历史中。
5. **动作注册键**：必须跟随窗口当前会话；切换会话后旧键失效、新键生效。
6. **命令短路回归**：`ShouldContinue=false` 的命令仍要发布 `CommandResultEvent`。

---

### Task 1: 契约 `CommandAction` 与事件透传

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Commands/CommandAction.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Commands/ICommand.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Commands/CommandActionPassthroughTests.cs`

**Interfaces:**
- Produces:
  - `CommandAction { string Name; IReadOnlyDictionary<string, object?> Payload; }`
  - `CommandActionNames.ModelPicker` / `ModelSet` / `AgentPicker` / `AgentSet` / `PageNavigate` / `Notice` / `SessionNew` / `SessionFork` / `SessionClear`
  - `CommandResult.Action`
  - `CommandResultEvent.Action`
  - `ExecutionRunner.CreateCommandResultEvent(string sessionId, string cmdName, CommandResult result)`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Commands;

public class CommandActionPassthroughTests
{
    [Fact]
    public void CreateCommandResultEvent_Should_Carry_Action()
    {
        var action = new CommandAction
        {
            Name = CommandActionNames.ModelSet,
            Payload = new Dictionary<string, object?> { ["modelId"] = "m1" }
        };
        var result = new CommandResult { Success = true, Message = "ok", Action = action };

        var evt = ExecutionRunner.CreateCommandResultEvent("s1", "model", result);

        evt.CommandName.Should().Be("model");
        evt.Action.Should().NotBeNull();
        evt.Action!.Name.Should().Be(CommandActionNames.ModelSet);
        evt.Action.Payload!["modelId"].Should().Be("m1");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests`
Expected: 编译失败（`CommandAction` / `Action` / `CreateCommandResultEvent` 不存在）。

- [ ] **Step 3: 实现契约与透传**

- 新建 `CommandAction.cs`：`public sealed record CommandAction { public required string Name { get; init; } public IReadOnlyDictionary<string, object?> Payload { get; init; } = Empty; public static IReadOnlyDictionary<string, object?> Empty { get; } = new Dictionary<string, object?>(); }`
- 同文件 `public static class CommandActionNames`，常量值按 spec §5.3。
- `CommandResult` 增加 `public CommandAction? Action { get; init; }`。
- `MessageEventTypes.cs` 顶部加 `using Seeing.Agent.Abstractions.Commands;`；`CommandResultEvent` 增加 `public CommandAction? Action { get; init; }`。
- 在 `ExecutionRunner` 中把现有事件构造抽成 `internal static CommandResultEvent CreateCommandResultEvent(string sessionId, string cmdName, CommandResult result)`，保留原来所有字段并加 `Action = result.Action`；`ProcessCommandAsync` 改调用它。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests`
Run: `dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~CommandActionPassthroughTests"`
Expected: 1 passed。

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Commands/CommandAction.cs src/abstractions/Seeing.Agent.Abstractions/Commands/ICommand.cs src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs tests/hosting/Seeing.Agent.Hosting.Tests/Commands/CommandActionPassthroughTests.cs
git commit -m "feat(commands): 命令动作契约与事件透传"
```

---

### Task 2: WebUI 宿主动作注册表

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/HostActionRegistry.cs`
- Modify: `samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/HostActionRegistryTests.cs`

**Interfaces:**
- Produces:
  - `enum HostActionStatus { Handled, Unavailable, NotHandled }`
  - `record HostActionResult(HostActionStatus Status, string? Message = null)`
  - `interface IHostActionRegistry { IDisposable Register(string sessionId, string actionName, Func<CommandAction, Task<HostActionResult>> handler); Task<HostActionResult> InvokeAsync(string sessionId, CommandAction action); }`
  - DI：`AddScoped<IHostActionRegistry, HostActionRegistry>()`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class HostActionRegistryTests
{
    [Fact]
    public async Task Invoke_Unknown_Action_Should_Return_NotHandled()
    {
        var registry = new HostActionRegistry();
        var result = await registry.InvokeAsync("s1", new CommandAction { Name = "nope" });
        result.Status.Should().Be(HostActionStatus.NotHandled);
    }

    [Fact]
    public async Task Invoke_Should_Isolate_By_Session()
    {
        var registry = new HostActionRegistry();
        using var _ = registry.Register("s1", "x", _ => Task.FromResult(new HostActionResult(HostActionStatus.Handled)));

        (await registry.InvokeAsync("s1", new CommandAction { Name = "x" })).Status
            .Should().Be(HostActionStatus.Handled);
        (await registry.InvokeAsync("s2", new CommandAction { Name = "x" })).Status
            .Should().Be(HostActionStatus.NotHandled);
    }

    [Fact]
    public async Task ReRegister_Same_Key_Should_Let_Last_Win_And_Old_Dispose_Should_Not_Remove_New()
    {
        var registry = new HostActionRegistry();
        var first = registry.Register("s1", "x", _ => Task.FromResult(new HostActionResult(HostActionStatus.Handled, "first")));
        using var second = registry.Register("s1", "x", _ => Task.FromResult(new HostActionResult(HostActionStatus.Handled, "second")));
        first.Dispose();

        var result = await registry.InvokeAsync("s1", new CommandAction { Name = "x" });
        result.Message.Should().Be("second");
    }

    [Fact]
    public async Task Handler_Throw_Should_Return_NotHandled()
    {
        var registry = new HostActionRegistry();
        using var _ = registry.Register("s1", "boom", _ => throw new InvalidOperationException("x"));
        var result = await registry.InvokeAsync("s1", new CommandAction { Name = "boom" });
        result.Status.Should().Be(HostActionStatus.NotHandled);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现注册表**

- 单文件定义枚举、结果 record、接口、实现，命名空间 `Seeing.Agent.WebUI.Services`。
- 内部 `Dictionary<(string SessionId, string ActionName), Entry>`，`lock` 保护；`Entry` 记录 handler 与递增的 `RegistrationId`。
- `Register` 返回 `IDisposable`；Dispose 时只在当前 Entry 仍是自己时移除（避免旧句柄删掉新注册）。
- 重复注册记录 Warning（注入 `ILogger<HostActionRegistry>?`，测试可传 null）。
- `InvokeAsync` 找不到 → `NotHandled`；handler 抛异常 → 记录日志并 `NotHandled`。
- `WebUiHost.cs` 服务注册区（`MessageTimelineStore` 附近）加 `builder.Services.AddScoped<IHostActionRegistry, HostActionRegistry>();`。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~HostActionRegistryTests"`
Expected: 4 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/HostActionRegistry.cs samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/HostActionRegistryTests.cs
git commit -m "feat(webui): 宿主动作注册表"
```

---

### Task 3: 时间线临时系统条目

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTransientNoticeTests.cs`

**Interfaces:**
- Produces: `public void AppendTransientNotice(string key, string markdown, bool isError = false);`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class MessageTimelineStoreTransientNoticeTests
{
    [Fact]
    public void AppendTransientNotice_Should_Add_System_Item()
    {
        var store = new MessageTimelineStore();
        store.AppendTransientNotice("cmd:x", "**hello**");

        var item = store.Items.Should().ContainSingle(i => i.Key == "cmd:x").Subject;
        item.Kind.Should().Be(TimelineItemKind.System);
        item.Message!.Role.Should().Be("system");
        item.Message.Content.Should().Be("**hello**");
    }

    [Fact]
    public void ResetFromSession_Should_Drop_Transient_Items()
    {
        var store = new MessageTimelineStore();
        store.AppendTransientNotice("cmd:x", "hello");

        store.ResetFromSession(Array.Empty<SessionMessage>(), "s1");

        store.Items.Should().NotContain(i => i.Key == "cmd:x");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（方法不存在）。

- [ ] **Step 3: 实现**

- 在 `MessageTimelineStore` 加方法，构造 `MessageViewModel { Id = key, SessionId = _boundSessionId ?? "", Role = "system", Content = markdown, IsComplete = true, Timestamp = DateTime.Now }`。
- 若 key 已存在则先移除（避免重复）；追加 `TimelineItem { Key = key, Kind = TimelineItemKind.System, Message = vm }` 到 `_items`，写入 `_itemByMessageId`，调用 `RefreshTailHint()` 与 `RaiseChanged()`。
- `isError` 传给 `MessageViewModel` 现有错误样式字段（实现时按 `MessageViewModelFactory`/`ErrorMessageComponent` 的字段对齐；若没有独立字段，用 metadata/内容前缀并在此任务内补最小字段）。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~MessageTimelineStoreTransientNoticeTests"`
Expected: 2 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs samples/Seeing.Agent.WebUI/Models/MessageViewModel.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTransientNoticeTests.cs
git commit -m "feat(webui): 命令结果临时系统条目"
```

---

### Task 4: 命令结果分派器

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/CommandResultDispatcher.cs`
- Modify: `samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/CommandResultDispatcherTests.cs`

**Interfaces:**
- Consumes: `IHostActionRegistry`、`MessageTimelineStore`
- Produces: `public sealed class CommandResultDispatcher { Task DispatchAsync(string sessionId, CommandResultEvent evt); }`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class CommandResultDispatcherTests
{
    private static (CommandResultDispatcher Dispatcher, MessageTimelineStore Store, HostActionRegistry Registry) Create()
    {
        var store = new MessageTimelineStore();
        var registry = new HostActionRegistry();
        return (new CommandResultDispatcher(registry, store, NullLogger<CommandResultDispatcher>.Instance), store, registry);
    }

    private static CommandResultEvent Evt(string? message, CommandAction? action = null)
        => new() { SessionId = "s1", CommandName = "cron-list", Message = message, Action = action };

    [Fact]
    public async Task No_Action_With_Message_Should_Append_Notice()
    {
        var (dispatcher, store, _) = Create();
        await dispatcher.DispatchAsync("s1", Evt("job-1"));
        store.Items.Should().ContainSingle(i => i.Message!.Content == "job-1");
    }

    [Fact]
    public async Task Handled_Action_With_Message_Should_Append_Action_Message_Not_Raw_Message()
    {
        var (dispatcher, store, registry) = Create();
        using var _ = registry.Register("s1", "model.picker", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled, "已打开模型选择器")));

        await dispatcher.DispatchAsync("s1", Evt("很长的模型列表", new CommandAction { Name = "model.picker" }));

        store.Items.Should().ContainSingle(i => i.Message!.Content == "已打开模型选择器");
        store.Items.Should().NotContain(i => i.Message!.Content == "很长的模型列表");
    }

    [Fact]
    public async Task Unavailable_Action_Should_Show_Reason()
    {
        var (dispatcher, store, registry) = Create();
        using var _ = registry.Register("s1", "model.set", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Unavailable, "ACP 会话不可切换模型")));

        await dispatcher.DispatchAsync("s1", Evt("回退文本", new CommandAction { Name = "model.set" }));

        store.Items.Should().ContainSingle(i => i.Message!.Content == "ACP 会话不可切换模型");
        store.Items.Should().NotContain(i => i.Message!.Content == "回退文本");
    }

    [Fact]
    public async Task NotHandled_Action_Should_Fall_Back_To_Raw_Message()
    {
        var (dispatcher, store, _) = Create();
        await dispatcher.DispatchAsync("s1", Evt("回退文本", new CommandAction { Name = "unknown" }));
        store.Items.Should().ContainSingle(i => i.Message!.Content == "回退文本");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现分派器**

- 构造注入 `IHostActionRegistry`、`MessageTimelineStore`、`ILogger<CommandResultDispatcher>`。
- `DispatchAsync` 规则：
  - `Action != null` → `InvokeAsync`；
    - `Handled`：若 `result.Message` 非空 → `AppendTransientNotice($"cmd:{evt.CommandName}:{evt.Timestamp.Ticks}", result.Message, !evt.Success)`；否则不显示原文。
    - `Unavailable`：显示 `result.Message ?? evt.Message ?? "当前宿主不支持该操作"`。
    - `NotHandled`：显示 `evt.Message`（非空时）。
  - `Action == null`：`evt.Message` 非空时显示。
  - 每条命令只追加一次（`Handled` 且无消息时不追加）。
- `WebUiHost.cs` 加 `builder.Services.AddScoped<CommandResultDispatcher>();`。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~CommandResultDispatcherTests"`
Expected: 4 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/CommandResultDispatcher.cs samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/CommandResultDispatcherTests.cs
git commit -m "feat(webui): 命令结果分派与回落"
```

---

### Task 5: SessionWindow 接线（分派 + 4 个动作 + 注册跟随会话）

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/CommandActionPolicy.cs`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/CommandActionPolicyTests.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionWindowCommandDispatchContractTests.cs`

**Interfaces:**
- Consumes: `CommandResultDispatcher`、`IHostActionRegistry`、`CommandActionNames`
- Produces:
  - `static Task<HostActionResult> CommandActionPolicy.ModelSetAsync(string? modelId, bool isAcpPassthrough, Func<string, Task>? setModel)`
  - `static Task<HostActionResult> CommandActionPolicy.AgentSetAsync(string? agentId, bool isAcpPassthrough, Func<string, Task>? setAgent)`
  - 窗口注册 `page.navigate`、`notice`、`model.set`、`agent.set`

- [ ] **Step 1: 写失败测试**

```csharp
// tests/apps/Seeing.Agent.WebUI.Tests/Services/CommandActionPolicyTests.cs
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class CommandActionPolicyTests
{
    [Fact]
    public async Task ModelSet_Acp_Should_Be_Unavailable()
    {
        var result = await CommandActionPolicy.ModelSetAsync("m1", isAcpPassthrough: true, _ => Task.CompletedTask);
        result.Status.Should().Be(HostActionStatus.Unavailable);
        result.Message.Should().Contain("ACP");
    }

    [Fact]
    public async Task ModelSet_Empty_Should_Be_NotHandled()
    {
        var result = await CommandActionPolicy.ModelSetAsync(" ", isAcpPassthrough: false, _ => Task.CompletedTask);
        result.Status.Should().Be(HostActionStatus.NotHandled);
    }

    [Fact]
    public async Task ModelSet_Normal_Should_Invoke_Delegate_And_Handle()
    {
        string? applied = null;
        var result = await CommandActionPolicy.ModelSetAsync("m1", false, id => { applied = id; return Task.CompletedTask; });
        applied.Should().Be("m1");
        result.Status.Should().Be(HostActionStatus.Handled);
    }

    [Fact]
    public async Task AgentSet_Acp_Should_Be_Unavailable()
    {
        var result = await CommandActionPolicy.AgentSetAsync("build", isAcpPassthrough: true, _ => Task.CompletedTask);
        result.Status.Should().Be(HostActionStatus.Unavailable);
    }
}
```

```csharp
// tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionWindowCommandDispatchContractTests.cs
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

public class SessionWindowCommandDispatchContractTests
{
    [Fact]
    public void CommandResult_Should_Go_Through_Dispatcher()
    {
        var razor = File.ReadAllText(Path.Combine(RepoRoot(), "samples", "Seeing.Agent.WebUI", "Components", "SessionWindow.razor"));
        razor.Should().Contain("CommandResultDispatcher");
        razor.Should().Contain("DispatchAsync");
    }

    [Fact]
    public void Window_Should_Register_Core_Actions_And_Rebind_On_Session_Change()
    {
        var razor = File.ReadAllText(Path.Combine(RepoRoot(), "samples", "Seeing.Agent.WebUI", "Components", "SessionWindow.razor"));
        razor.Should().Contain("CommandActionNames.PageNavigate");
        razor.Should().Contain("CommandActionNames.ModelSet");
        razor.Should().Contain("CommandActionNames.AgentSet");
        razor.Should().Contain("_actionsSessionId");
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~CommandActionPolicyTests|FullyQualifiedName~SessionWindowCommandDispatchContractTests"`
Expected: 编译失败/断言失败。

- [ ] **Step 3: 实现分派纯函数与窗口接线**

- `CommandActionPolicy`：按 Step 1 的用例实现四个分支（ACP → `Unavailable`；空参数/委托为空 → `NotHandled`；正常 → 调用委托并返回 `Handled` + 提示）。
- 注入 `CommandResultDispatcher CommandDispatcher`、`IHostActionRegistry HostActions`。
- `OnHandlerChanged` 内：`if (evt is CommandResultEvent commandEvt) await CommandDispatcher.DispatchAsync(EffectiveSessionId, commandEvt);`；保留现有 `NavigationTarget` 导航分支与 `_sync.ProcessEvent`。
- 新增 `EnsureHostActions`：记录 `_actionsSessionId` 与 `List<IDisposable>`；当 `EffectiveSessionId` 变化时先撤销全部再按新 id 注册 `page.navigate`、`notice`、`model.set`、`agent.set`（`Dispose` 里撤销）。
- 处理器：
  - `page.navigate`：payload `path` 为空 → `NotHandled`；否则 `NavigationManager.NavigateTo(path)` → `Handled`。
  - `notice`：payload `text`，返回 `Handled` + text（由分派器统一追加，避免重复）。
  - `model.set`：委托 `CommandActionPolicy.ModelSetAsync(modelId, Context.IsAcpPassthrough, Context.SetModelAsync)`。
  - `agent.set`：委托 `CommandActionPolicy.AgentSetAsync(agentId, Context.IsAcpPassthrough, Context.SetAgentAsync)`。
- 在 `SyncContextState()` 末尾调用 `EnsureHostActions()`（会话 id 已更新）；`OnInitialized` 后再调一次。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~CommandActionPolicyTests|FullyQualifiedName~SessionWindowCommandDispatchContractTests"`
Expected: 6 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/CommandActionPolicy.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/CommandActionPolicyTests.cs tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionWindowCommandDispatchContractTests.cs
git commit -m "feat(webui): 会话窗口执行命令动作"
```

---

### Task 6: 模型选择器动作

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/Models/ModelSelectBadge.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/ComposerModelPicker.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Components/ComposerModelPickerActionContractTests.cs`

**Interfaces:**
- Consumes: `IHostActionRegistry`、`CommandActionNames.ModelPicker`
- Produces: `ModelSelectBadge` 的 `public Task OpenAsync();`

- [ ] **Step 1: 写源码契约测试**

```csharp
[Fact]
public void Composer_Should_Register_ModelPicker_Action()
{
    var razor = Read("Components", "ComposerModelPicker.razor");
    razor.Should().Contain("CommandActionNames.ModelPicker");
    razor.Should().Contain("OpenAsync");
    razor.Should().Contain("_registeredSessionId");
}

[Fact]
public void Badge_Should_Expose_Public_Open()
{
    Read("Components", "Models", "ModelSelectBadge.razor").Should().Contain("public async Task OpenAsync()");
}
```

（`Read` 复用 `ModelSelectBadgeContractTests` 的 `ReadWebUiFile` 模式。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests` + vstest filter。
Expected: 失败。

- [ ] **Step 3: 实现**

- `ModelSelectBadge`：把现有 `OpenModalAsync` 改为 `public async Task OpenAsync()`（内部逻辑不变），按钮 `OnClick` 改指向它。
- `ComposerModelPicker`：注入 `IHostActionRegistry`；模板里给 `<ModelSelectBadge @ref="_badge" ...>`；在 `OnParametersSet` 按 `Context.SessionId` 重注册 `model.picker`（同 Task 5 的换键规则），处理器调用 `_badge?.OpenAsync()`，返回 `HostActionResult(Handled)`；`Dispose` 撤销。

- [ ] **Step 4: 运行测试确认通过**

Run: 同上，Expected: 2 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/Models/ModelSelectBadge.razor samples/Seeing.Agent.WebUI/Components/ComposerModelPicker.razor tests/apps/Seeing.Agent.WebUI.Tests/Components/ComposerModelPickerActionContractTests.cs
git commit -m "feat(webui): /model 打开模型选择器"
```

---

### Task 7: Agent 选择器动作与工具栏解耦

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionToolbarAgentPickerContractTests.cs`

**Interfaces:**
- Consumes: `IHostActionRegistry`、`CommandActionNames.AgentPicker`、`Context.ShowModelSelector`（Task 8 后该标记由 `CanEdit` 控制）
- Produces: Agent `Select` 受控 `Open`；徽标与选择器独立渲染

- [ ] **Step 1: 写源码契约测试**

```csharp
[Fact]
public void Toolbar_Should_Register_AgentPicker_And_Control_Open()
{
    var razor = Read("Components", "SessionToolbar.razor");
    razor.Should().Contain("CommandActionNames.AgentPicker");
    razor.Should().Contain("Open=\"_agentPickerOpen\"");
    razor.Should().Contain("_registeredSessionId");
}

[Fact]
public void Agent_Select_Should_Not_Be_In_Else_Of_SubAgent_Badge()
{
    var razor = Read("Components", "SessionToolbar.razor");
    razor.Should().NotContain("else if (_optionsReady && Context.CurrentSession != null)");
}
```

- [ ] **Step 2: 运行确认失败**

Run: 同上。Expected: 失败。

- [ ] **Step 3: 实现**

- 注入 `IHostActionRegistry`；字段 `_agentPickerOpen`、注册句柄、`_registeredSessionId`。
- 拆开模板：`ShowSubAgentBadge` 只控制徽标/只读元信息块；Agent `Select` 独立渲染，条件为 `Context.ShowModelSelector && _optionsReady && Context.CurrentSession != null`（`ShowModelSelector` 在 Task 8 后由 `CanEdit` 控制，未接管子会话不显示；ACP 会话仍允许切 Agent）。
- `Select` 加 `Open="_agentPickerOpen"` 与 `OpenChanged` 回写。
- 按 `Context.SessionId` 重注册 `agent.picker`，处理器置 `_agentPickerOpen = true` 并返回 `Handled`；`Dispose` 撤销。

- [ ] **Step 4: 运行测试确认通过**

Run: 同上。Expected: 2 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionToolbarAgentPickerContractTests.cs
git commit -m "feat(webui): /agent 展开 Agent 选择器"
```

---

### Task 8: 子会话默认只读、接管后放开

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionWindowContextTests.cs`

**Interfaces:**
- Produces:
  - `ApplyCapabilities(bool isChild, bool hasParent, SessionOwner owner)`
  - `bool CanEdit`
  - `bool CanTakeOver`
  - `Task OnTakeOverAsync()`（窗口私有）

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionWindowContextTests
{
    [Fact]
    public void Child_Not_Taken_Over_Should_Be_Read_Only_And_Offer_Takeover()
    {
        var ctx = new SessionWindowContext();
        ctx.ApplyCapabilities(isChild: true, hasParent: true, owner: SessionOwner.Parent);

        ctx.CanEdit.Should().BeFalse();
        ctx.CanTakeOver.Should().BeTrue();
        ctx.ShowModelSelector.Should().BeFalse();
        ctx.CanRename.Should().BeFalse();
        ctx.CanClear.Should().BeFalse();
        ctx.CanBranch.Should().BeFalse();
        ctx.CanCreateSession.Should().BeFalse();
        ctx.CanEditOutboundBinding.Should().BeFalse();
        ctx.AllowAutoApproveChange.Should().BeFalse();
        ctx.ShowAcpModeSelector.Should().BeFalse();
    }

    [Fact]
    public void Child_Taken_Over_Should_Be_Editable()
    {
        var ctx = new SessionWindowContext();
        ctx.ApplyCapabilities(true, true, SessionOwner.Human);

        ctx.CanEdit.Should().BeTrue();
        ctx.CanTakeOver.Should().BeFalse();
        ctx.ShowModelSelector.Should().BeTrue();
        ctx.CanRename.Should().BeTrue();
        ctx.CanClear.Should().BeTrue();
    }

    [Fact]
    public void Root_Session_Should_Always_Be_Editable()
    {
        var ctx = new SessionWindowContext();
        ctx.ApplyCapabilities(false, false, SessionOwner.Self);

        ctx.CanEdit.Should().BeTrue();
        ctx.CanTakeOver.Should().BeFalse();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`。Expected: 编译失败（签名不匹配）。

- [ ] **Step 3: 实现**

- `SessionWindowContext.ApplyCapabilities` 增加 `SessionOwner owner` 参数，计算 `CanEdit = !isChild || owner == SessionOwner.Human`；八项编辑能力全部赋 `CanEdit`；`CanTakeOver = isChild && owner != SessionOwner.Human`。保留子会话专属标记计算不变。
- `SessionWindow.SyncContextState`：先读 `control` 与 `owner`，再调用 `ApplyCapabilities(IsChildView, hasParent, owner)`。
- 子会话操作条（现有“推送产出/交还父会话”区域）增加“接管”按钮：`if (Context.CanTakeOver)` 渲染，点击 `OnTakeOverAsync`。
- `OnTakeOverAsync`：`session.SetOwner(SessionOwner.Human, "webui")` → `UpdatedAt` → `SessionManager.SaveAsync` → `MessageService.Success("已接管子会话")` → `SyncContextState()` → `StateHasChanged`。
- `OnReleaseToParent` 保持现有实现（调用 `SyncContextState` 即可让 `CanEdit` 收回）。

- [ ] **Step 4: 运行测试确认通过**

Run: vstest filter `SessionWindowContextTests`。Expected: 3 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionWindowContextTests.cs
git commit -m "feat(webui): 子会话默认只读、接管后放开编辑"
```

---

### Task 9: 命令改造（/model、/agent、/mcp）

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Commands/AgentCommands.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Commands/ToolsCommands.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Commands/AgentCommandsTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Mcp.Tests/ToolsCommandsMcpTests.cs`

**Interfaces:**
- Consumes: `CommandActionNames`、`CommandAction`
- Produces: 三个命令的行为按 spec §7

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Hosting.Commands;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Commands;

public class AgentCommandsTests
{
    private static AgentCommands Create()
    {
        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.GetPrimaryAgentsAsync())
            .ReturnsAsync(new List<AgentDefinition> { new() { Name = "build" } });

        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GetAvailableModels())
            .Returns(new Dictionary<string, ModelConfig> { ["m1"] = new() { Id = "m1", Name = "M1" } });

        return new AgentCommands(agents.Object, llm.Object);
    }

    [Fact]
    public void Model_No_Args_Should_Request_Picker()
    {
        var result = Create().SwitchModel(new CommandContext());
        result.Action!.Name.Should().Be(CommandActionNames.ModelPicker);
        result.ShouldContinue.Should().BeFalse();
    }

    [Fact]
    public void Model_With_Args_Should_Request_Set_And_Not_Claim_Switched()
    {
        var result = Create().SwitchModel(new CommandContext { Arguments = "m1" });
        result.Action!.Name.Should().Be(CommandActionNames.ModelSet);
        result.Action.Payload!["modelId"].Should().Be("m1");
        result.Message.Should().NotContain("Switched to model");
    }

    [Fact]
    public async Task Agent_With_Args_Should_Request_Set()
    {
        var result = await Create().SwitchAgent(new CommandContext { Arguments = "build" });
        result.Action!.Name.Should().Be(CommandActionNames.AgentSet);
        result.Action.Payload!["agentId"].Should().Be("build");
    }
}
```

Tools 测试追加：

```csharp
result.Action!.Name.Should().Be(CommandActionNames.PageNavigate);
result.Action.Payload!["path"].Should().Be("/mcp");
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests`。Expected: 编译失败（构造函数参数不匹配/断言失败）。

- [ ] **Step 3: 实现**

- `AgentCommands`：移除 `IAgentRuntimeManager` 依赖（构造函数改为 `(IAgentRegistry, ILlmService)`）。
  - `SwitchModel`：无参 → `CommandResult { Success=true, Message=list, ShouldContinue=false, Action=ModelPicker }`；有参且存在 → `CommandResult { Success=true, Message=$"请在支持交互的宿主中切换模型：{targetName}", ShouldContinue=false, Action=ModelSet(modelId) }`；不存在 → 现有 `Fail`。
  - `SwitchAgent`：无参 → `Action=AgentPicker` + 列表；有参且存在 → `Action=AgentSet(agentId)` + 提示；不再调用 `SetDefaultAgentAsync`。
- `ToolsCommands.ManageMcp`：无参/list 分支在原结果上加 `Action = new CommandAction { Name = PageNavigate, Payload = { ["path"] = "/mcp" } }`；其他分支不变。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests` + vstest filter `AgentCommandsTests`。
Run: `dotnet build tests/capabilities/Seeing.Agent.Mcp.Tests` + vstest filter `ToolsCommandsMcpTests`。
Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Commands/AgentCommands.cs src/hosting/Seeing.Agent.Hosting/Commands/ToolsCommands.cs tests/hosting/Seeing.Agent.Hosting.Tests/Commands/AgentCommandsTests.cs tests/capabilities/Seeing.Agent.Mcp.Tests/ToolsCommandsMcpTests.cs
git commit -m "feat(commands): 交互命令改为声明宿主动作"
```

---

### Task 10: 全量验证与文档

**Files:**
- Modify: `docs/superpowers/specs/2026-11-18-webui-command-host-actions-design.md`（状态改为“已实现”）
- Test: 全量构建与目标测试程序集

- [ ] **Step 1: 全量构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 errors。

- [ ] **Step 2: 运行相关测试程序集**

Run:
```bash
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Mcp.Tests/bin/Debug/net10.0/Seeing.Agent.Mcp.Tests.dll
```
Expected: 全绿；新增用例全部通过。

- [ ] **Step 3: 手动验证**

启动 `dotnet run --project samples/Seeing.Agent.WebUI`，逐项确认：
- `/model` → 打开模型弹窗；选择后会话模型改变。
- `/model <id>` → 会话模型改变并出现临时提示。
- `/agent` → 展开 Agent 下拉；选择后改变。
- `/mcp` → 跳到 `/mcp` 页面。
- `/cron-list`、`/tools`、`/help` → 时间线出现临时系统条目；刷新页面后消失。
- 子会话：默认编辑控件全部禁用；点“接管”后放开；点“交还父会话”后再次禁用。
- 子会话未接管时直接发消息 → 自动接管，编辑控件随事件放开。
- ACP 透传会话 `/model xxx` → 显示“ACP 透传会话的模型由后端决定…”。

- [ ] **Step 4: 更新设计文档状态**

把头部 `状态：待批准` 改为 `状态：已实现（P1）`，并记录手动验证结果。

- [ ] **Step 5: 提交**

```bash
git add docs/superpowers/specs/2026-11-18-webui-command-host-actions-design.md
git commit -m "docs: 命令宿主动作 P1 实现完成"
```
