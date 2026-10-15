# Session Title Ensuring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在执行环旁路用直接 LLM 自动生成 ≤15 字会话标题，删除 title Agent，WebUI 实时更新且展示截断。

**Architecture:** `ISessionTitleEnsuring`（Seeing.Agent）用 `ITextCompletion` 一次补全；`ExecutionJobService` fire-and-forget 调用并发布 `SessionTitleChangedEvent`；`SetTitleAsync` 补发 Session 层 Updated 事件；删掉旧 Hook/`title` Agent。

**Tech Stack:** .NET 10、xUnit、FluentAssertions、Moq、Blazor WebUI

## Global Constraints

- **NO GIT COMMITS** — 用户明确要求全程不提交。禁止 `git commit` / 为提交而 `git add`。
- Spec: `docs/superpowers/specs/2026-08-04-session-title-ensuring-design.md`
- 标题硬上限 **≤15 字符**（落库不加 `...`；展示层 CSS ellipsis）
- 失败静默，保留占位标题
- 模型：`TitleGeneration.Model` ?? 会话当前模型
- 触发：`ExecutionJobService` fork，非 Hook / 非插件
- 直接 LLM：用现有窄接口 `ITextCompletion`（spec 写 ILlmService；本计划选用已有窄接口，等价且更合适）
- 删除：`title` BuiltInAgent、`TitleGenerationService`、`ITitleGenerationService`
- TFM: `net10.0`

## File map

| Path | Role |
|------|------|
| `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs` | 增加 `TitleGenerationOptions` |
| `src/Seeing.Agent/Services/ISessionTitleEnsuring.cs` | 接口 |
| `src/Seeing.Agent/Services/SessionTitleEnsuring.cs` | 实现：条件 / prompt / CleanTitle / CAS |
| `src/Seeing.Agent/Services/TitlePrompts.cs` | 固定 system prompt |
| `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | 注册新服务；删除旧 TitleGeneration DI |
| `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` | 删除 `CreateTitleAgent` |
| Delete `src/Seeing.Agent/Services/TitleGenerationService.cs` | — |
| Delete `src/Seeing.Session/Management/ITitleGenerationService.cs` | — |
| `src/Seeing.Session/Management/SessionManager.cs` | `SetTitleAsync` 补发 `SessionEvent` |
| `src/Seeing.Agent.App/Events/AppEvents.cs` | `SessionTitleChangedEvent` + `AppEventType` |
| `src/Seeing.Agent.App/Execution/ExecutionJobService.cs` | fork ensure + 发事件 |
| `samples/.../Pages/Session.razor` | 处理标题事件 |
| `samples/.../wwwroot/css/session-page.css` | 页头 ellipsis |
| `samples/.../Pages/SessionsPage.razor` | Tooltip + 确认 ellipsis |
| `tests/Seeing.Agent.Tests/Services/SessionTitleEnsuringTests.cs` | 单元测试 |

---

### Task 1: Options + CleanTitle/ShouldEnsure 纯逻辑测试与实现骨架

**Files:**
- Modify: `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs`
- Create: `src/Seeing.Agent/Services/TitlePrompts.cs`
- Create: `src/Seeing.Agent/Services/ISessionTitleEnsuring.cs`
- Create: `src/Seeing.Agent/Services/SessionTitleEnsuring.cs`（先实现静态/可测方法 + 空壳 TryEnsure）
- Test: `tests/Seeing.Agent.Tests/Services/SessionTitleEnsuringTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class TitleGenerationOptions
{
    public bool Enabled { get; set; } = true;
    public string? Model { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
}

// on SeeingAgentOptions:
public TitleGenerationOptions TitleGeneration { get; set; } = new();

public interface ISessionTitleEnsuring
{
    Task<string?> TryEnsureAsync(
        string sessionId,
        string userMessage,
        string? fallbackModelId,
        CancellationToken cancellationToken = default);
}
```
- `SessionTitleEnsuring` 暴露 `internal static` 或 `public` 测试友好方法：`IsDefaultTitle`、`ShouldEnsure`、`CleanTitle`（若保持 private，测试同程序集用 InternalsVisibleTo — 项目已有）

- [ ] **Step 1: 写失败测试**

```csharp
public class SessionTitleEnsuringTests
{
    [Theory]
    [InlineData("Session abc", true)]
    [InlineData("新会话", true)]
    [InlineData("New Session", true)]
    [InlineData("", true)]
    [InlineData("调试生产错误", false)]
    public void IsDefaultTitle_cases(string title, bool expected)
        => SessionTitleEnsuring.IsDefaultTitle(title).Should().Be(expected);

    [Fact]
    public void CleanTitle_truncates_to_15_chars_without_ellipsis()
    {
        var raw = "这是一个超过十五个字的超长标题内容继续";
        var cleaned = SessionTitleEnsuring.CleanTitle(raw);
        cleaned.Length.Should().BeLessOrEqualTo(15);
        cleaned.Should().NotContain("...");
    }

    [Fact]
    public void CleanTitle_takes_first_line_and_strips_quotes()
    {
        SessionTitleEnsuring.CleanTitle("\"短标题\"\n第二行").Should().Be("短标题");
    }

    [Fact]
    public void ShouldEnsure_false_when_disabled_or_fork_or_not_default_or_count_not_1()
    {
        SessionTitleEnsuring.ShouldEnsure(
            enabled: false, kind: SessionKind.Root, parentId: null,
            title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

        SessionTitleEnsuring.ShouldEnsure(
            enabled: true, kind: SessionKind.Root, parentId: "p",
            title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

        SessionTitleEnsuring.ShouldEnsure(
            enabled: true, kind: SessionKind.SubAgent, parentId: null,
            title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeFalse();

        SessionTitleEnsuring.ShouldEnsure(
            enabled: true, kind: SessionKind.Root, parentId: null,
            title: "已有标题", realUserCount: 1, userMessage: "hi").Should().BeFalse();

        SessionTitleEnsuring.ShouldEnsure(
            enabled: true, kind: SessionKind.Root, parentId: null,
            title: "新会话", realUserCount: 2, userMessage: "hi").Should().BeFalse();

        SessionTitleEnsuring.ShouldEnsure(
            enabled: true, kind: SessionKind.Root, parentId: null,
            title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeTrue();
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SessionTitleEnsuringTests"
```

Expected: FAIL（类型/方法不存在）

- [ ] **Step 3: 实现 Options + TitlePrompts + 静态判定/清洗 + 接口**

`TitlePrompts.System` 文案迁自原 title Agent，把「≤50」改为「≤15 字」。

`CleanTitle`：去 `<think>...</think>`（及旧 think 变体）、取首个非空行、去两侧引号、硬截断 `Math.Min(15, length)`。

`TryEnsureAsync` 本 Task 可先 `throw new NotImplementedException()` 或 return null — 下一 Task 补全。

`SeeingAgentOptions` 增加属性：

```csharp
public TitleGenerationOptions TitleGeneration { get; set; } = new();
```

- [ ] **Step 4: 再跑测试 — PASS**

- [ ] **Step 5: Do NOT commit**

---

### Task 2: SessionTitleEnsuring.TryEnsureAsync（ITextCompletion + CAS）

**Files:**
- Modify: `src/Seeing.Agent/Services/SessionTitleEnsuring.cs`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（注册；删除旧 TitleGeneration 工厂）
- Delete: `src/Seeing.Agent/Services/TitleGenerationService.cs`
- Delete: `src/Seeing.Session/Management/ITitleGenerationService.cs`
- Modify: `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs`（删除 CreateTitleAgent 及 yield）
- Test: 扩展 `SessionTitleEnsuringTests.cs`

**Interfaces:**
- Consumes: `ITextCompletion.CompleteAsync(system, user, model, ct)`
- Consumes: `ISessionManager.Get` / `SetTitleAsync`
- Consumes: `IOptionsMonitor<SeeingAgentOptions>`
- Produces: `TryEnsureAsync` → 写入成功返回 cleaned title，否则 `null`

**TryEnsureAsync 逻辑（必须按序）：**

1. `Enabled` 检查  
2. `Get(sessionId)`  
3. `ShouldEnsure(...)`（含 Kind/Parent/默认标题/真实用户消息数/非空）  
4. `model = options.Model ?? fallbackModelId`；空则 Warning + return null  
5. 用户消息截断至 1000 字符  
6. `using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); cts.CancelAfter(TimeSpan.FromSeconds(timeout));`  
7. `await _text.CompleteAsync(TitlePrompts.System, truncated, model, maxTokens: 32, cts.Token)` — 请求侧限制生成量（非 2048 后再截断）；catch 全部 Exception → Warning + null  
8. `CleanTitle`（≤15 兜底）；空 → null  
9. 再 `Get`；若 `!IsDefaultTitle` → null（CAS）  
10. `await SetTitleAsync(sessionId, cleaned)`  
11. return cleaned  

DI 注册（替换旧块）：

```csharp
services.AddSingleton<ISessionTitleEnsuring, SessionTitleEnsuring>();
```

删除 `TitleGenerationService` 工厂与 `ITitleGenerationService` 映射。

- [ ] **Step 1: 写失败测试（Moq）**

```csharp
[Fact]
public async Task TryEnsureAsync_writes_title_when_conditions_met()
{
    var session = SessionData.Create();
    session.Title = "新会话";
    session.Kind = SessionKind.Root;
    session.Messages.Add(SessionMessage.UserMessage("debug 500 errors in production"));

    var sm = new Mock<ISessionManager>();
    sm.Setup(x => x.Get(session.Id)).Returns(session);
    sm.Setup(x => x.SetTitleAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .Returns(Task.CompletedTask)
        .Callback<string, string, CancellationToken>((_, t, _) => session.Title = t);

    var text = new Mock<ITextCompletion>();
    text.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync("调试生产500错误");

    var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
    opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

    var svc = new SessionTitleEnsuring(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleEnsuring>.Instance);
    var title = await svc.TryEnsureAsync(session.Id, "debug 500 errors in production", "provider/model");

    title.Should().Be("调试生产500错误");
    session.Title.Should().Be("调试生产500错误");
}

[Fact]
public async Task TryEnsureAsync_skips_when_user_already_renamed()
{
    var session = SessionData.Create();
    session.Title = "新会话";
    session.Kind = SessionKind.Root;
    session.Messages.Add(SessionMessage.UserMessage("hi"));

    var sm = new Mock<ISessionManager>();
    sm.Setup(x => x.Get(session.Id)).Returns(session);

    var text = new Mock<ITextCompletion>();
    text.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync("问候")
        .Callback(() => session.Title = "我手动改的"); // LLM 返回前用户已改名

    var opts = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
    opts.Setup(x => x.CurrentValue).Returns(new SeeingAgentOptions());

    var svc = new SessionTitleEnsuring(text.Object, sm.Object, opts.Object, NullLogger<SessionTitleEnsuring>.Instance);
    var title = await svc.TryEnsureAsync(session.Id, "hi", "m");

    title.Should().BeNull();
    sm.Verify(x => x.SetTitleAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

- [ ] **Step 2: 跑测试 — 期望 FAIL 或部分 FAIL**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SessionTitleEnsuringTests"
```

- [ ] **Step 3: 实现 TryEnsureAsync + DI + 删除旧代码/title Agent**

在 `BuiltInAgents` 中删除 `CreateTitleAgent` 方法，并去掉 `yield return CreateTitleAgent();`。

全文搜索确认无残留 `ITitleGenerationService` / `TitleGenerationService` / `\"title\"` agent 注册引用（测试里若有 mock title agent 一并删）。

- [ ] **Step 4: 测试 PASS；`dotnet build` 通过**

- [ ] **Step 5: Do NOT commit**

---

### Task 3: SetTitleAsync 发 SessionEvent + App 层 SessionTitleChangedEvent + ExecutionJobService 挂接

**Files:**
- Modify: `src/Seeing.Session/Management/SessionManager.cs`（`SetTitleAsync`）
- Modify: `src/Seeing.Agent.App/Events/AppEvents.cs`
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`
- Modify: `src/Seeing.Agent.App/ChatOrchestratorServiceExtensions.cs` 或 Execution 注册处（若 JobService 需新依赖，走 `IServiceProvider` 解析即可，与 InstructionManager 同模式）

**Interfaces:**
- Produces:
```csharp
// AppEventType
SessionTitleChanged = 1004,

public record SessionTitleChangedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public MessageEventType Type => (MessageEventType)AppEventType.SessionTitleChanged;
    public required string Title { get; init; }
}
```

**SetTitleAsync 补齐**（在 Save 前后均可，对齐 `AddMessageAsync`）：

```csharp
_eventPublisher?.Publish(new SessionEvent
{
    SessionId = session.Id,
    Type = SessionEventType.Updated,
    Data = session
});
```

**ExecutionJobService.ProcessExecutionAsync** — 在 `EnsureSessionAsync` 之后、主 `ExecuteAsync` 之前插入：

```csharp
var userText = record.Input?.Text;
if (!string.IsNullOrWhiteSpace(userText) && options skip 用户消息未发生时也要谨慎：
    // 仅当本次 Submit 持久化了用户消息时才 ensure；
    // 若 record.Options?.SkipUserMessagePersist == true 则跳过
)
{
    var titleEnsuring = scope.ServiceProvider.GetRequiredService<ISessionTitleEnsuring>();
    var fallbackModel = context.RequestModelId ?? session.SelectedModel;
    _ = EnsureTitleFireAndForgetAsync(
        titleEnsuring, record.SessionId, userText!, fallbackModel);
}

async Task EnsureTitleFireAndForgetAsync(
    ISessionTitleEnsuring ensuring, string sid, string text, string? model)
{
    try
    {
        var title = await ensuring.TryEnsureAsync(sid, text, model, CancellationToken.None);
        if (!string.IsNullOrEmpty(title))
        {
            _eventPublisher.Publish(sid, new SessionTitleChangedEvent
            {
                SessionId = sid,
                Title = title
            });
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Title ensure failed: {SessionId}", sid);
    }
}
```

注意：`BuildExecutionContextAsync` 在 ensure 调用前完成，以便拿到 `RequestModelId`。若 `userText` 为空但有多模态 parts，用 `BuildUserMessage` 同类逻辑拼一段文本（或取 parts 中 text）；无文本则跳过。

- [ ] **Step 1: 若有现成 Execution/App 测试则补一条「发布 SessionTitleChangedEvent」；否则本 Task 以编译 + 手工/后续 WebUI 验证为主，至少加一个针对 Ensure 辅助逻辑的轻量测试或 SessionManager SetTitle 事件测试（Session.Tests）**

Session 侧最小测试思路：mock `ISessionEventPublisher`，`SetTitleAsync` 后 Verify Publish 一次 Updated。

- [ ] **Step 2: 实现挂接与事件类型**

- [ ] **Step 3:**

```bash
dotnet build Seeing.Agent.slnx
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SessionTitleEnsuringTests"
dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~SetTitle"
```

（若尚无 SetTitle 测试，新建 `SessionManagerSetTitleTests`）

- [ ] **Step 4: Do NOT commit**

---

### Task 4: WebUI 事件处理 + 展示截断

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`（事件分支 + 页头 Tooltip）
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/session-page.css`（`.session-title` ellipsis）
- Modify: `samples/Seeing.Agent.WebUI/Pages/SessionsPage.razor`（Tooltip 包标题）
- 若侧栏有会话列表组件显示 Title，同样加 Tooltip（搜索 `session.Title` / `session-title-text`）

**Session.razor 事件处理**（在 `SubscribeEvents` 循环内，`SessionUpdatedEvent` 分支旁）：

```csharp
else if (evt is SessionTitleChangedEvent titleEvt)
{
    if (SessionState.CurrentSession != null &&
        SessionState.CurrentSession.Id == titleEvt.SessionId)
    {
        SessionState.CurrentSession.Title = titleEvt.Title;
    }
    await InvokeAsync(StateHasChanged);
}
```

**不要**对 `SessionTitleChangedEvent` 调用 `ResetTimelineFromSession`。

页头 markup：

```razor
<Tooltip Title="@SessionState.Title">
    <span Class="session-title">@SessionState.Title</span>
</Tooltip>
```

CSS（`session-page.css`）：

```css
.session-title {
    font-size: var(--font-size-lg);
    font-weight: 600;
    color: var(--color-text);
    margin-left: var(--space-2);
    max-width: min(40vw, 280px);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    display: inline-block;
    vertical-align: bottom;
}
```

SessionsPage：用 `<Tooltip Title="@session.Title">` 包裹已有 `.session-title`（该页已有 ellipsis）。

- [ ] **Step 1: 改 UI/CSS**

- [ ] **Step 2: `dotnet build samples/Seeing.Agent.WebUI`**

- [ ] **Step 3: 手工验证清单**
  1. 新建会话（标题「新会话」）→ 发首条消息 → 数秒内页头变为 ≤15 字标题  
  2. 手动改名后再发消息 → 标题不被覆盖  
  3. 超长标题（可临时 mock）→ 页头省略，Tooltip 全文  
  4. Agents 管理页无 `title` 项  

- [ ] **Step 4: Do NOT commit**

---

### Task 5: 清理残留与回归

**Files:**
- Grep 清理：`title agent`、`TitleGenerationService`、`ITitleGenerationService`、`CreateTitleAgent`
- 按需更新 `AGENTS.md` / `CLAUDE.md` 中提及 title Agent 的一行说明（非阻塞，有则改）

- [ ] **Step 1:**

```bash
rg -n "TitleGenerationService|ITitleGenerationService|CreateTitleAgent|\"title\" Agent" --glob "*.cs" --glob "*.md"
```

删光代码残留；文档顺手改。

- [ ] **Step 2:**

```bash
dotnet test tests/Seeing.Agent.Tests
dotnet test tests/Seeing.Session.Tests
dotnet build Seeing.Agent.slnx
```

Expected: 全绿（或仅与本改动无关的既有失败需注明）

- [ ] **Step 3: Do NOT commit**

---

## Spec coverage checklist

| Spec 项 | Task |
|---------|------|
| 执行环 fork 触发 | Task 3 |
| ISessionTitleEnsuring + 直接 LLM | Task 1–2（ITextCompletion） |
| 配置 Enabled/Model/Timeout | Task 1–2 |
| ≤15 字 | Task 1 |
| CAS / 失败静默 | Task 2 |
| 删 title Agent / 旧服务 | Task 2 |
| SetTitle SessionEvent | Task 3 |
| SessionTitleChangedEvent | Task 3–4 |
| WebUI 更新 + ellipsis | Task 4 |
| 测试矩阵 | Task 1–2、5 |
| 不 git commit | Global + 每 Task Step |

## Type consistency

- `TryEnsureAsync(...) -> Task<string?>`
- `SessionTitleChangedEvent.Title: string`
- `AppEventType.SessionTitleChanged = 1004`
- `TitleGenerationOptions` 挂在 `SeeingAgentOptions.TitleGeneration`
