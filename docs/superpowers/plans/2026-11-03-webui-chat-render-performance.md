# WebUI 聊天渲染性能优化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除「长会话流式卡顿」与「多 Agent 会话切换等待」，把 SessionWindow 的渲染宽度、Summary 窗口的加载范围、以及流式期每 delta 的重建成本压到与内容增量成正比。

**Architecture:** 五层递进。① **加载边界**：Summary 窗口不再承担 Full 窗口职责（不建时间线、不跑预算；执行态事件保留但只放行 4 类）。② **窗口生命周期**：切换 active 时不销毁重建整窗，改为参数更新 + 补齐本地状态重置。③ **渲染粒度**：Loop 内按消息分段缓存，只重建变化的那条。④ **流式渲染策略**：流式期跳过 Markdown 全量重解析。⑤ **热路径数据结构**：消息快照缓存、时间线字典索引、渲染合并、滚动互操作限流。

**Tech Stack:** .NET 10 / Blazor Server (`AddServerSideBlazor`)、xUnit v3、FluentAssertions 8、AntDesign 1.6.2、Markdig 1.3.2

**Spec:** 本计划「诊断」节（含已核实证据）+ `docs/architecture/04-development-standards.md`

---

## 诊断（已用代码核实，实施时不要重复调查）

### 已推翻的原假设

**原假设「一个 delta 触发 3 次全树重渲染」不成立。** 三处 `StateHasChanged` 都在同一 Dispatcher 同步栈上（`SessionWindow.razor:1016` 的 `await InvokeAsync` 内联执行 → `_sync` 的回调 `:643` → `:1052`），Blazor 的 `ComponentBase._hasPendingQueuedRender` 会把它们合并成**一次渲染通道**。真问题是**这一次通道渲染的子树过大**（Toolbar + PromptInput + TodoPanel + MessageList 全走一遍），不是次数多。因此 `RenderCoalescer` 的定位必须从「3→1」降级为「封顶高频 delta 的渲染速率」。

### 确认的主瓶颈（按对用户诉求的影响排序）

| # | 病灶 | 证据 | 影响 |
|---|---|---|---|
| **D-1** | **Summary 侧栏窗口跑 Full 窗口全流程**：`LoadCommandsFromBackend` + `LoadSessionByIdAsync`（含全量 Timeline 构建 + 全量 token 估算 + `ReconcileIncompleteTasks`）+ **`EnsureStreamBinding`**（订阅事件流并每 delta 跑时间线同步）+ 预算订阅，全部在 `ViewMode` 判断之外 | `SessionWindow.razor:418, 424, 436, 443` | **多 agent 卡顿首因**。N 个子会话 = N 份完整时间线 + N 个事件流消费者在同一个 circuit 上争抢 Dispatcher |
| **D-2** | **`@key` 整窗销毁重建**：每次切 active 销毁重建 `SessionWindow` → 新 `EventStreamHandler` 订阅即**重放 ≤100 条环形缓冲事件** + 子组件全量挂载（Toolbar 的 `LoadOptionsAsync`、PromptInput 的 4 次 JS 初始化、MessageList 的 `initMessageListScroll`）+ `TaskCardAggregator.Rebind/Reconcile` 全表扫 | `Session.razor:85, 164`；`ExecutionEventPublisher.cs:73-83`；`SessionEventStreamRouter.cs:137-163` | **切换等待首因**。且因为侧栏也在 `@key`，一次切换 = **两个**会话各跑一遍 `OnInitializedAsync` |
| **D-3** | **流式期全文重解析 Markdown**：每次可见 delta 对全文跑 `Markdown.ToHtml`，累计 O(L²) | `TextBlockRenderer.cs:42`；`MemoryRenderCache.cs:86-103` | **长回答 CPU 绝对大头** |
| **D-4** | **每次 delta 重建整个 Loop 的 block**：`OrderBy(Step)` + 为 Loop 内每条历史消息重造 block + 重拼字符串 Id + 每块新建参数字典 | `LoopGroupItem.razor:32-39`；`ContentBlock.cs:341-358`；`MessageComponentRegistry.cs:256` | **多步工具调用时线性放大** |
| **D-5** | **每 delta 全表扫描时间线**：`AbsorbOrphanSinglesInto` 倒扫全 `_items` + 无条件 `RenumberLoopIndexes` 再全扫；`FindTurnByKey` 用 `LastOrDefault`；`ReconcileAppendFromSession` 每次 `messages.ToList()` + 建全量 HashSet | `MessageTimelineStore.cs:378-390, 517-543, 630-632, 711-744` | 长会话 O(n²) 累积 |
| **D-6** | **会话切换重复工作**：`ResetFromSession` 可能执行两次（`:741` 与 `:1656`）；`GetBudgetStatus` 在无 usage 缓存时算两遍 breakdown | `SessionWindow.razor:741, 1658`；`TokenBudgetManager.cs:62, 84` | 切换固定开销 |
| **D-7** | **滚动 JS 往返与渲染串行** | `MessageList.razor:241, 253, 289` | 流式发涩 |
| **D-8** | **聚合器同步等待**：子会话每事件 `_writeLock.Wait()`，并在 Dispatcher 上执行 | `TaskCardAggregator.cs:120` | 多 agent 并发时排队 |

### 确认的非瓶颈（不要浪费时间）

- `EventStreamHandler.OnEvent` 的 fire-and-forget**当前安全**：`ProcessEventAsync`（`:204-311`）全程无 `await`，同步返回 `Task.CompletedTask`，不会乱序也不会堆积。但一旦有人加 `await` 就会变成炸弹 —— 本计划要求给它加断言注释，不改签名。
- `SessionGroup.Clone()` 的成本被高估：O(M)，M ≤ 10。
- `SessionData.Messages` 的 `ToList()` 本身很便宜（500 引用 ≈ 1µs）；它的价值是为 D-5 提供索引底座，不是直接省时间。
- 关系条/抽屉的 O(M²) LINQ：`HasRelatedSessions` 为 false 时不渲染，抽屉默认关闭。属收尾顺手优化。

---

## Global Constraints

- 语言：**全部中文**（注释、日志、UI 文案），与现有文件风格一致。
- `samples/Seeing.Agent.WebUI` **不使用 CPM**（`ManagePackageVersionsCentrally=false`），新增包必须写显式版本。
- 不改 `src/abstractions` 的公共契约签名。`src/primitives/Seeing.Session` 的 `Messages` 保持 `IReadOnlyList<SessionMessage>` 签名。
- **线程契约**：`MessageTimelineStore` 只允许在 Blazor Dispatcher 线程变更。新增索引不得引入跨线程写。
- **不得引入静默吞异常**（`docs/architecture/04` §反模式）：新增的 `catch` 必须记日志或至少 `Debug.WriteLine`。
- 流式渲染跳过 Markdown 后，**消息完成后必须走一次完整 Markdown 渲染**。
- 测试命令（MTP 在本机无法发现测试，一律用 VSTest）：
  - 构建：`dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
  - 运行：`dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~<过滤词>"`
- 每个 Task 结束都要 `dotnet build Seeing.Agent.slnx` 通过再提交。
- **每个 Task 必须独立可编译、可 `git revert`**（不产生跨任务的编译依赖）。

## Review Focus

最容易被人当成「跑起来了」而实际坏掉的地方：

1. **流式结束后永久纯文本**：`Block.IsStreaming` 依赖 `MessageViewModel.IsComplete`，但分段缓存判的是 `MessageViewModel.Revision`。任何改了 VM 可见字段却没 `Touch()` 的路径都会让缓存命中旧片段。
2. **Summary 窗口去掉事件流后状态不更新**：需要有一条替代的状态来源（组事件 / 父窗口推进），否则侧栏卡片会永远停在旧状态。
3. **切换后残留上一个会话的执行态**：从「执行中的 A」切到「空闲的 B」时，B 若显示「执行中」且取消按钮绑定的是 A 的 CTS，点取消会停错会话。
4. **流式纯文本塌行**：`.message-content` 没有 `white-space: pre-wrap`（`wwwroot/css/message-item.css:268`），直接把原始文本塞进 div 会把所有换行折叠。
5. **`MessageList` 停止自渲染后不再推进**：父级传给它的参数在流式期间全部不变，Blazor 会跳过 `SetParametersAsync`，导致组件完全不重渲染、`OnAfterRenderAsync` 不执行。

---

## 收益矩阵（诚实版）

| Task | 场景① 长会话流式 | 场景② 多 agent 切换 |
|---|---|---|
| 0 基线采集 | — | — |
| 1 Summary 窗口轻量化 | 中等 | **显著** |
| 2 切换不再整窗重建 | 无 | **显著** |
| 3 流式跳过 Markdown | **显著** | 无 |
| 4 Loop 分段缓存 | **显著** | 轻微 |
| 5 时间线索引化 | **显著** | 轻微 |
| 6 渲染合并（降级） | 轻微~中等 | 轻微 |
| 7 多 agent 热路径 | 中等 | 中等 |
| 8 滚动 JS 限流 | 中等 | 无 |
| 9 消息快照 + Id 索引 | 轻微（为 5/7 提供底座） | 轻微 |
| 10 关系模型预计算 | 无 | 无（收尾可选项） |

**重复计算提示**：Task 6 的收益部分被 Task 8 覆盖（渲染次数降 → JS 往返自然降）；Task 9 的收益依附于 Task 5/7，不能单独按「显著」累加；Task 1 做完后 Task 10 基本无收益。

---

## Task 0: 基线采集与验收口径

**Files:**
- Create: `docs/superpowers/plans/2026-11-03-webui-chat-render-performance-results.md`

**Interfaces:** 无代码产出，仅建立可比对的基线。

- [ ] **Step 1: 建立固定测试语料**

在真实工作区里构造两个会话（不要用脚本造假数据，要能触发真实渲染路径）：

- **会话 A（长会话）**：500+ 消息，其中至少 3 轮包含多步工具调用（同一 Loop 内 step 0/1/2），最后一条 assistant 回答 ≥ 3000 字符。
- **会话 B（多 agent 组）**：1 个主会话 + 3 个子会话，让 3 个子会话同时执行。

- [ ] **Step 2: 加临时打点（提交时移除）**

在 `SessionWindow.razor` 的 `OnAfterRenderAsync` 里加：

```csharp
protected override void OnAfterRender(bool firstRender)
{
    if (firstRender && _switchStopwatch is not null)
    {
        _switchStopwatch.Stop();
        Console.WriteLine($"[PERF] session-window-first-render: {_switchStopwatch.ElapsedMilliseconds}ms, session={SessionId}, mode={ViewMode}");
        _switchStopwatch = null;
    }
}
```

在 `LoadSessionByIdAsync` 入口 `_switchStopwatch = Stopwatch.StartNew();`。

- [ ] **Step 3: 采集 6 组基线数据**

每组 10 次，取 P50 / P90，填入结果文件：

1. 会话 A 冷进入（从首页点进来）耗时
2. 会话 A 内流式输出 10 秒期间的渲染通道数（`RenderCoalescer` 未接入前，在 `SessionWindow.OnAfterRender` 里累加计数并在 Consle 每秒输出一次）
3. 会话 A 流式输出的浏览器 DevTools：`Recalculate Style` + `Layout` 次数、长任务（>50ms）个数
4. 会话 B 中在主区与侧栏之间切换 active 10 次，每次的 `session-window-first-render` 耗时
5. 会话 B 三子会话同时流式时，主区 UI 的一次点击响应延迟（用 `Stopwatch` 包住 `OnTileClick` 到首帧）
6. 会话 B 切换时侧栏 Summary 窗口的重建次数（给 `SessionWindow.OnInitializedAsync` 加计数日志）

- [ ] **Step 4: 提交基线结果**

```bash
git add docs/superpowers/plans/2026-11-03-webui-chat-render-performance-results.md
git commit -m "docs: 记录 WebUI 渲染性能基线数据"
```

> 后续每个 Task 完成后，在同一文件追加「改造后」对应项，形成前后对照。

---

## Task 1: Summary 窗口轻量化

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:402-461`
- Create: `samples/Seeing.Agent.WebUI/Services/SessionSummaryProjector.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSummaryProjectorTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct SessionSummarySnapshot(SummaryState StateKind, string Preview, string ActionLine, string SourceLine, string StatusBarClass)`
  - `static SessionSummarySnapshot SessionSummaryProjector.Build(SessionData? session, bool isExecuting, ExecutionStatus? status, string? parentTitle, SessionRelation relation, bool isAnchor, string? successorTitle)`

- [ ] **Step 1: 写失败测试**

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSummaryProjectorTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using System.Collections.Immutable;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>会话摘要投影：预览、动作行、状态条的纯函数判定。</summary>
public class SessionSummaryProjectorTests
{
    private static SessionData Session(params SessionMessage[] messages)
    {
        var session = SessionData.Create();
        foreach (var m in messages) session.AddMessage(m);
        return session;
    }

    private static SessionSummarySnapshot Build(
        SessionData? session, bool isExecuting = false,
        ExecutionStatus? status = ExecutionStatus.Completed,
        string? parentTitle = null,
        SessionRelation relation = SessionRelation.None,
        bool isAnchor = true)
        => SessionSummaryProjector.Build(session, isExecuting, status, parentTitle, relation, isAnchor, successorTitle: null);

    [Fact]
    public void Build_NullSession_ShouldReturnPlaceholder()
        => Build(null).Preview.Should().Be("暂无消息");

    [Fact]
    public void Build_EmptySession_ShouldReturnPlaceholder()
        => Build(Session()).Preview.Should().Be("暂无消息");

    [Fact]
    public void Build_WithAssistantMessage_ShouldPreviewLatestAssistant()
    {
        var s = Session(
            new SessionMessage { Id = "u1", Role = MessageRole.User, Content = "提问" },
            new SessionMessage { Id = "a1", Role = MessageRole.Assistant, Content = "这是回答内容" });

        Build(s).Preview.Should().Contain("这是回答内容");
    }

    [Fact]
    public void Build_WithRunningTasks_ShouldReportSubAgentCount()
    {
        var s = Session(new SessionMessage
        {
            Id = "a1", Role = MessageRole.Assistant, Content = "",
            ToolCalls = ImmutableList.Create(
                new SessionToolCall { Id = "t1", Name = "task", Status = "running", TaskId = "c1" },
                new SessionToolCall { Id = "t2", Name = "task", Status = "running", TaskId = "c2" })
        });

        var snapshot = Build(s, isExecuting: true, status: ExecutionStatus.Running);

        snapshot.ActionLine.Should().Contain("2 个子代理");
        snapshot.StateKind.Should().Be(SummaryState.Running);
    }

    [Fact]
    public void Build_WithRunningTool_ShouldReportToolName()
    {
        var s = Session(new SessionMessage
        {
            Id = "a1", Role = MessageRole.Assistant, Content = "",
            ToolCalls = ImmutableList.Create(new SessionToolCall { Id = "t1", Name = "bash", Status = "running" })
        });

        Build(s, isExecuting: true, status: ExecutionStatus.Running).ActionLine.Should().Contain("bash");
    }

    [Theory]
    [InlineData(ExecutionStatus.Failed, "conference-tile--failed")]
    [InlineData(ExecutionStatus.Cancelled, "conference-tile--cancelled")]
    [InlineData(ExecutionStatus.Completed, "conference-tile--done")]
    public void Build_Status_ShouldMapStatusBarClass(ExecutionStatus status, string expected)
        => Build(Session(), status: status).StatusBarClass.Should().Be(expected);

    [Fact]
    public void Build_WithParent_ShouldProduceSourceLine()
        => Build(Session(), parentTitle: "父会话", relation: SessionRelation.Child, isAnchor: false)
            .SourceLine.Should().Contain("父会话");
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`SessionSummaryProjector` 未定义）。

- [ ] **Step 3: 实现投影器**

新建 `samples/Seeing.Agent.WebUI/Services/SessionSummaryProjector.cs`。实现要点：

- `StateKind` = `SessionSummaryView.Classify(isExecuting, status)`（`SessionSummaryView.cs:45`）
- 一次遍历 `session?.Messages`（Task 9 已缓存快照），同时得到「最后一条非摘要 assistant」与「运行中工具集合」
- `Preview`：优先最后 assistant 的 `Content` 经 `SessionSummaryView.PreviewSnippet`；否则运行中工具名（`正在执行工具: X`）；否则 `暂无消息`
- `ActionLine`：运行中 `task` 工具按 `TaskId` 去重计数 → `⚙ 正在执行 task · N 个子代理`；否则首个运行中工具名 → `⚙ 正在执行 {name}`；否则 `⚙ 正在执行`
- `SourceLine`：直接复用 `SessionSummaryView.SourceLine(RelationshipSource, string?)`（`:72`）。方向由 `relation`/`isAnchor` 推导：`isAnchor` → `None`；`HandoffPredecessor` → `HandoffTo`；其余非 `None` → `Source`
- `StatusBarClass`：`isExecuting || status in {Running, Pending}` → `conference-tile--running`；`Failed`/`Cancelled` 对应类；其余 `conference-tile--done`
- **不输出 `BadgeText`**：徽标依赖 `SessionRelationView.BadgeText(...)`，留在 `SessionWindow.GetRelationBadgeText()`（O(1)，无需缓存）

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionSummaryProjectorTests"`
Expected: 9 个测试全部 PASS（Theory 计 3 个）。

- [ ] **Step 5: Summary 模式跳过 Full 专有流程**

修改 `SessionWindow.razor` 的 `OnInitializedAsync`（`:402-461`），把 Full 专有步骤用 `ViewMode` 包裹：

```csharp
protected override async Task OnInitializedAsync()
{
    _globalSessionState = GlobalSessionState;
    EnsureSyncForSession(SessionId);

    _globalAutoApprove = SeeingAgentOptions.CurrentValue.Permission?.AutoApproveAll ?? false;
    _seeingAgentOptionsSubscription = SeeingAgentOptions.OnChange((opts, name) => _ = InvokeAsync(() =>
    {
        _globalAutoApprove = opts.Permission?.AutoApproveAll ?? false;
        StateHasChanged();
    }));

    var effectiveSessionId = SessionId;

    if (ViewMode == Services.SessionWindowMode.Full)
    {
        await LoadCommandsFromBackend();
        if (!string.IsNullOrEmpty(SessionId))
        {
            await LoadSessionByIdAsync(SessionId);
            _loadedSessionId = SessionId;
        }
        else
        {
            effectiveSessionId = await LoadRecentOrEmptyAsync();
        }

        if (!string.IsNullOrEmpty(effectiveSessionId))
        {
            EnsureStreamBinding(effectiveSessionId);
            RestoreExecutionFromOverview(effectiveSessionId);
        }

        SubscribeToBudgetUpdates();
        SubscribeToSessionEvents();
        Context.SessionId = effectiveSessionId ?? "";
        WireContextCallbacks();
    }
    else
    {
        // Summary 卡片：只加载会话快照 + 摘要投影，不建时间线、不跑预算；
        // 事件流仍绑定但由 OnHandlerChanged 按 ViewMode 过滤，只保留执行态/标题类事件
        await LoadSummaryOnlyAsync();
        SubscribeToSessionEvents();   // 标题变更仍需要
    }
}
```

从原 `OnInitializedAsync` 末尾抽出 `WireContextCallbacks()`（`:446-460` 的 `Context.SetAgentAsync = ...` 等赋值）作为私有方法，供两个分支共用。

新增 `LoadSummaryOnlyAsync()`：

```csharp
/// <summary>
/// Summary 卡片专用加载：只取会话快照并计算摘要投影，不建时间线、不估预算。
/// 侧栏卡片的职责是「展示标题/状态/预览」，不需要完整消息渲染管道。
/// <para>事件流仍需绑定（执行态要靠它更新），但 <see cref="OnHandlerChanged"/> 会按
/// ViewMode 过滤掉除执行态/标题之外的全部事件，时间线管道不会被执行。</para>
/// </summary>
private async Task LoadSummaryOnlyAsync()
{
    if (string.IsNullOrEmpty(SessionId))
        return;

    IsLoadingSession = true;
    var sessionData = await SessionDirectory.GetAsync(SessionId);
    if (sessionData is null)
    {
        ErrorMessage = $"找不到会话: {SessionId}";
        IsLoadingSession = false;
        return;
    }

    SessionState.SetSession(sessionData);
    _loadedSessionId = SessionId;

    var group = await GroupManager.GetGroupForSessionAsync(SessionId);
    _groupMember = group?.Members.FirstOrDefault(m => string.Equals(m.SessionId, SessionId, StringComparison.Ordinal));
    _relation = _groupMember?.Relation
        ?? (sessionData.Kind == SessionKind.SubAgent ? SessionRelation.Child : SessionRelation.None);
    _currentGroup = group;
    _isAnchor = _groupMember?.IsAnchor ?? false;
    _parentSessionId = await GroupManager.GetParentAsync(SessionId);

    _renderedTitle = sessionData.Title;
    RebuildSummarySnapshot();
    SyncContextState();
    IsLoadingSession = false;

    // 执行态事件是卡片状态唯一可靠的来源（组事件与 registry 都不含执行态）
    EnsureStreamBinding(SessionId);
    RestoreExecutionFromOverview(SessionId);
}
```

- [ ] **Step 6: 用投影器替换渲染期 LINQ**

1. 新增字段 `private Services.SessionSummarySnapshot? _summarySnapshot;`
2. 新增 `RebuildSummarySnapshot()`（调用 `SessionSummaryProjector.Build(...)`，参数取 `SessionState.CurrentSession`、`SessionState.IsExecuting`、`SessionState.ExecutionStatus`、`SessionManager.Get(_parentSessionId)?.Title`、`_relation`、`_isAnchor`、`GetSuccessorTitle()`）
3. `SyncContextState()` 末尾：`if (ViewMode != Services.SessionWindowMode.Full) RebuildSummarySnapshot();`
4. 标记替换（`:238-296`）：
   - `GetStatusBarClass()` → `_summarySnapshot?.StatusBarClass ?? "conference-tile--done"`
   - `GetCurrentActionLine()` → `_summarySnapshot?.ActionLine ?? "⚙ 正在执行"`
   - `GetLatestAssistantPreview()` → `_summarySnapshot?.Preview ?? "暂无消息"`
   - `GetSourceLine()` → `_summarySnapshot?.SourceLine ?? ""`
5. 删除 `GetCurrentActionLine()` / `GetLatestAssistantPreview()` / `GetStatusBarClass()` 私有方法。保留 `GetElapsedText()`（O(1)）。

- [ ] **Step 7: 验证**

Run: `dotnet build Seeing.Agent.slnx`

手工验证：
1. 多 agent 组（≥3 子会话）下打开 `/conference`
2. 侧栏卡片的标题、状态徽标、运行中动作、预览摘要、来源行全部正确
3. 子会话完成后侧栏变两行紧凑卡；失败/取消状态条颜色正确
4. **关键**：三个子会话同时流式时，侧栏卡片状态与耗时**实时更新**（执行态由 Step 8 的轻量事件过滤提供）
5. **关键**：三个子会话同时流式时，侧栏卡片**不应出现内容预览频繁跳变**（说明时间线管道已被正确掐掉）

- [ ] **Step 8: 为 Summary 建立轻量事件过滤（必做）**

**为什么不用「父级传执行态」方案**：`SessionGroupChangedEvent`（`src/abstractions/.../Events/ISessionGroupEventBus.cs:24-43`）与 `SessionGroup.Clone()`（`src/primitives/Seeing.Session/Core/SessionGroup.cs:36-47`）只携带**组成员关系**（SessionId / Relation / ParentSessionId / Label / IsAnchor / Order），**不含执行态**；`SessionWindowRegistry.Windows` 的 `IsExecuting` 恒为 `false`（`SessionWindowRegistry.cs:238` 注释明确「registry 不持权威执行态」）；父页面的 `_executionStates` 又只由各窗口的 `OnExecutionStateChanged` 上报，而该上报依赖 `SessionState.IsExecuting/ExecutionStatus`。Summary 一旦失去事件流就不知道自己在执行 → 不会上报 → 父级也就拿不到。**这是循环依赖，父级传参方案技术上不成立。**

因此 Summary 必须保留订阅，但只放行 4 类事件。**关键在于过滤必须在 `InvokeAsync` 之前**，否则回放期仍会把事件排进 Dispatcher 队列：

```csharp
private async Task OnHandlerChanged(IMessageEvent? evt)
{
    if (evt == null)
        return;

    // 会话归属过滤（防御：handler 已按会话绑定，理论上不会收到其他会话事件）
    if (!string.IsNullOrEmpty(evt.SessionId)
        && !string.Equals(evt.SessionId, SessionState.SessionId, StringComparison.Ordinal))
        return;

    // Summary 卡片只关心执行态与标题；其余事件在最外层丢弃，不进入 Dispatcher 队列。
    // 订阅建立时的历史回放（≤100 条）会走到这里并在此被拦掉，不产生渲染开销。
    var isFull = ViewMode == Services.SessionWindowMode.Full;
    if (!isFull
        && evt is not (ExecutionStartedEvent or ExecutionCompleteEvent
                       or LoopCompleteEvent or SessionTitleChangedEvent))
        return;

    await InvokeAsync(() =>
    {
        // 真正的开销来源是时间线管道（Markdown 重建 + 分段渲染），Summary 必须跳过它。
        // 标题有两条独立通道：
        //   ① SessionTitleChangedEvent（执行流，自动生成标题）—— 原本在 _sync.ProcessEvent 内处理；
        //   ② SessionEvent.Updated（SessionEventPublisher，SetTitleAsync/保存时）—— 由
        //      OnSessionLifecycleEvent 独立处理，Summary 分支已通过 SubscribeToSessionEvents 订阅。
        // 跳过 _sync 后通道 ① 失效，这里显式补上。ApplyTitleToUi 自身按 _renderedTitle 去重，
        // 与通道 ② 重复触发无害。
        if (isFull)
        {
            _sync.ProcessEvent(evt);
        }
        else if (evt is SessionTitleChangedEvent titleEvt)
        {
            _sync.Title = titleEvt.Title;
            ApplyTitleToUi(titleEvt.Title);
        }

        EnsureRetryTimer();

        if (evt is ExecutionStartedEvent startedEvt)
        {
            SessionState.SyncExecutionStarted(startedEvt.ExecutionId);
            _executionStartedAtUtc ??= DateTime.UtcNow;
            SyncContextState();
        }

        if (evt is ExecutionCompleteEvent completeEvt && _handler != null && !_handler.IsStreamActive)
        {
            SessionState.ExecutionStatus = completeEvt.Status;
            SessionState.CurrentExecutionId = null;
            SessionState.CompleteExecution();
            _executionStartedAtUtc = null;
            SessionState.PendingInputCount = SessionRuntime.GetOverview(SessionState.SessionId).PendingInputCount;
            SyncContextState();
        }

        // 导航 / 压缩失败提示等副作用保持原有逻辑。Summary 因白名单过滤收不到这些事件，
        // 这些分支对 Summary 是空操作，保留不影响正确性。
        if (evt is NavigateEvent navEvt && !string.IsNullOrEmpty(navEvt.Target))
            NavigationManager.NavigateTo(navEvt.Target);
        else if (evt is CommandResultEvent commandEvt && !string.IsNullOrEmpty(commandEvt.NavigationTarget))
            NavigationManager.NavigateTo(commandEvt.NavigationTarget);
        else if (evt is ToolCallEvent handoffEvt && TryResolveHandoffTarget(handoffEvt, out var handoffTarget))
            NavigationManager.NavigateTo($"/session/{handoffTarget}");
        else if (evt is CompactionFailedEvent cfe && !string.IsNullOrEmpty(cfe.ErrorMessage))
            MessageService.Error($"压缩失败: {cfe.ErrorMessage}");

        // Summary 卡片也要渲染（执行态变化必须反映到卡片）。
        // Summary 的事件频率已被白名单压到极低，直接渲染即可，不必走合并器。
        if (isFull)
            _renderCoalescer?.Request();
        else
            StateHasChanged();
    });
}
```

并在 `LoadSummaryOnlyAsync()` 末尾补一次流绑定（Summary 需要执行态事件）：

```csharp
// Summary 仍需执行态事件来更新卡片状态与耗时；时间线管道已由 OnHandlerChanged 的
// ViewMode 过滤掐掉，因此这里的订阅成本只剩事件分发本身。
EnsureStreamBinding(SessionId);
RestoreExecutionFromOverview(SessionId);
```

> **为什么不担心「N 个子会话 = N 个 loop」**：`SessionEventStreamRouter.AttachConsumer`（`:99`）按 `sessionId` 建订阅，同会话的多 consumer 已共享同一个 `ConsumeLoopAsync`；不同子会话是不同 sessionId，loop 必然独立，任何方案都省不掉。本步掐掉的是**每事件的管道处理成本**，这才是真正的放大源。

- [ ] **Step 9: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SessionSummaryProjector.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor samples/Seeing.Agent.WebUI/Pages/Session.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSummaryProjectorTests.cs
git commit -m "perf(webui): Summary 侧栏窗口轻量化，跳过时间线管道只保留执行态事件"
```

---

## Task 2: 会话切换不再整窗重建

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor:83-91, 160-172`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:492-501`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:694-765`（抽出可复用步骤）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSwitchStateTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 Summary 轻量路径
- Produces:
  - `SessionWindow` 新增 `private void ResetLocalExecutionState()`（重置 `SessionState` 执行态与 CTS）
  - `SessionWindow.OnParametersSetAsync` 的会话切换分支补齐 4 步

- [ ] **Step 1: 写失败测试**

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSwitchStateTests.cs`（测试纯逻辑部分，组件行为靠手工验证）：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.WebUI.State;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>会话切换时的本地执行态重置：新会话不得继承上一会话的执行态与取消令牌。</summary>
public class SessionSwitchStateTests
{
    [Fact]
    public void Reset_AfterExecutingSession_ShouldClearExecutionState()
    {
        var state = new SessionState();
        state.SyncExecutionStarted("exec-1");
        state.ExecutionStatus = ExecutionStatus.Running;
        state.PendingInputCount = 3;

        state.ResetExecutionState();

        state.IsExecuting.Should().BeFalse();
        state.ExecutionStatus.Should().BeNull();
        state.CurrentExecutionId.Should().BeNull();
        state.PendingInputCount.Should().Be(0);
        state.HasActiveExecution.Should().BeFalse();
    }

    [Fact]
    public void Reset_ShouldReplaceCancellationTokenSource()
    {
        var state = new SessionState();
        state.StartExecution();
        var before = state.CancellationTokenSource;

        state.ResetExecutionState();
        state.StartExecution();

        state.CancellationTokenSource.Should().NotBeSameAs(before);
        before!.IsCancellationRequested.Should().BeTrue();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`ResetExecutionState` 未定义）。

- [ ] **Step 3: 在 SessionState 加重置方法**

`State/SessionState.cs` 新增（放在执行状态区域，参照 `CancelExecution` 与 `TryStartExecution` 的锁/CTS 处理）：

```csharp
/// <summary>
/// 切换会话时重置本地执行态：清空状态字段并替换取消令牌源。
/// <para>旧会话的 CTS 必须取消并释放，否则新会话的输入框会拿到旧令牌，
/// 点「取消」会停错会话。</para>
/// </summary>
public void ResetExecutionState()
{
    IsExecuting = false;
    IsCompleted = false;
    ExecutionStatus = null;
    CurrentExecutionId = null;
    PendingInputCount = 0;

    var previous = _cancellationTokenSource;
    if (previous is not null)
    {
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { /* 已释放，忽略 */ }
        previous.Dispose();
    }
    _cancellationTokenSource = null;
}
```

> 若 `StartExecution`/`TryStartExecution` 依赖 `_executionLock`，这里也要释放它（`if (_executionLock.CurrentCount == 0) _executionLock.Release();`），否则下一会话 `StartExecution` 会永久阻塞。**实现时先读 `SessionState.cs:205-260` 确认锁语义再写。**

- [ ] **Step 4: 抽出可复用的会话加载步骤**

在 `SessionWindow.razor` 里把 `LoadSessionByIdAsync`（`:694-765`）中与「窗口是否重建」无关的部分抽出为私有方法，供 `OnParametersSetAsync` 复用：

```csharp
/// <summary>加载会话数据并重建时间线（不处理事件流绑定与聚合器，由调用方负责）。</summary>
private async Task ApplySessionAsync(string sessionId)
{
    IsLoadingSession = true;
    var sessionData = await SessionDirectory.GetAsync(sessionId);
    if (sessionData is null)
    {
        ErrorMessage = $"找不到会话: {sessionId}";
        ShowEmptyState = true;
        IsLoadingSession = false;
        return;
    }

    _sync.CompactionInProgress = false;
    _sync.CompactionProgress = null;
    _sync.CompactionReasoning = null;
    _currentStreamingMessageId = string.Empty;
    _handler?.ClearCache();

    SessionState.SetSession(sessionData);
    _parentSessionId = await GroupManager.GetParentAsync(sessionId);
    var group = await GroupManager.GetGroupForSessionAsync(sessionId);
    _groupMember = group?.Members.FirstOrDefault(m => string.Equals(m.SessionId, sessionId, StringComparison.Ordinal));
    _relation = _groupMember?.Relation
        ?? (sessionData.Kind == SessionKind.SubAgent ? SessionRelation.Child : SessionRelation.None);
    _currentGroup = group;
    _isAnchor = _groupMember?.IsAnchor ?? false;
    _executionStartedAtUtc = null;
    _reportedExecuting = null;

    if (ViewMode == Services.SessionWindowMode.Full)
        _globalSessionState?.SetSession(sessionData);

    _renderedTitle = sessionData.Title;

    await ReconcileIncompleteTasksAsync();
    ResetTimelineFromSession();

    try { await EnsureDefaultAgentSelectionAsync(); }
    catch (Exception) { /* 默认 Agent/Model 解析失败不应阻断会话加载 */ }

    SyncContextState();
    ShowEmptyState = false;
    IsLoadingSession = false;

    _ = DeferBudgetUpdateAsync();
}
```

`LoadSessionByIdAsync` 简化为：`EnsureSyncForSession(sessionId); await ApplySessionAsync(sessionId);`

- [ ] **Step 5: 补齐 OnParametersSetAsync 切换分支**

`SessionWindow.razor:492-501` 改为：

```csharp
if (!string.IsNullOrEmpty(SessionId) && SessionId != _loadedSessionId)
{
    // 切换会话：必须在加载新会话前清空本地执行态，否则新会话会继承旧会话的
    // IsExecuting / ExecutionStatus / 取消令牌（点「取消」会停错会话）
    ResetLocalExecutionState();

    EnsureSyncForSession(SessionId);
    await ApplySessionAsync(SessionId);
    _loadedSessionId = SessionId;

    EnsureStreamBinding(SessionId);
    RestoreExecutionFromOverview(SessionId);

    // 以下 3 项原先只在本窗首次挂载时执行，复用窗口时必须补上，否则 UI 指向旧会话
    Context.SessionId = SessionId;          // 工具栏文件面板等按它定位会话
    SubscribeToBudgetUpdates();             // 预算徽标重绑到新会话
    await LoadCommandsFromBackend();         // 命令列表按新 Agent 的 Runtime 重新过滤
}
```

`ResetLocalExecutionState()` 实现为 `SessionState.ResetExecutionState()` + `_reportedExecuting = null` 的薄封装。

- [ ] **Step 6: 运行测试**

Run: `dotnet build Seeing.Agent.slnx` → `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionSwitchStateTests"`
Expected: 2 个测试 PASS。

- [ ] **Step 7: 去掉主窗 @key（关键改动）**

`Pages/Session.razor:85`：

```razor
@* 主窗：去掉 @key，切换 active 时复用窗口实例（走 OnParametersSetAsync 分支）。
   原实现 @key 会销毁重建整窗：新 handler 订阅即重放 ≤100 条缓冲事件 +
   Toolbar/PromptInput/MessageList 全量挂载 + 聚合器 Reconcile 全表扫。 *@
<SessionWindow SessionId="@_activeSessionId"
               ... />
```

**保留侧栏的 `@key="@entry.SessionId"`**（`:164`）：侧栏是列表 diff，需要 key 才能正确复用/卸载。

- [ ] **Step 8: 手工验证（本任务最关键的一节）**

1. **切回已看过的会话**：应显著变快（不再有新 handler 的事件回放）
2. **从执行中的 A 切到空闲的 B**：B 显示为空闲，取消按钮不可用；B 的输入框提交后作用于 B
3. **从空闲的 A 切到执行中的 B**：B 显示执行中，取消按钮可点且确实取消 B
4. **切换后立即提交消息**：消息进入正确的会话
5. **切换后打开文件面板**：面板指向新会话的工作区
6. **切换后 `/` 命令列表**：按新会话 Agent 的 Runtime 过滤（ACP ↔ Native 切换时可见差异）
7. **预算徽标**：切换后数字更新为新会话的预算，不串号
8. **滚动位置**：切换后应贴底显示最新消息（`Generation` 变化会触发重新贴底）
9. **侧栏 Summary 卡片**：切换后侧栏内容正确，无残留

- [ ] **Step 9: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/Session.razor samples/Seeing.Agent.WebUI/Components/SessionWindow.razor samples/Seeing.Agent.WebUI/State/SessionState.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSwitchStateTests.cs
git commit -m "perf(webui): 切换会话复用窗口实例，消除整窗重建与事件缓冲回放"
```

---

## Task 3: 流式跳过 Markdown 全量重解析

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Rendering/RenderMarkdownPolicy.cs`
- Modify: `samples/Seeing.Agent.WebUI/Rendering/Renderers/TextBlockRenderer.cs:30-47`
- Modify: `samples/Seeing.Agent.WebUI/Rendering/Renderers/ReasoningBlockRenderer.cs:30-60`
- Modify: `samples/Seeing.Agent.WebUI/Components/Messaging/TextMessageComponent.razor:5-33`
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/message-item.css:268`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Rendering/StreamingRenderTests.cs`（新建）

**Interfaces:**
- Produces: `static bool RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock block)`

- [ ] **Step 1: 写失败测试**

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Rendering/StreamingRenderTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>流式渲染策略：进行中跳过 Markdown，完成后使用 Markdown。</summary>
public class StreamingRenderTests
{
    [Theory]
    [InlineData(false)]
    public void ShouldUseMarkdown_StreamingText_ShouldBeFalse(bool complete)
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateText("x", 0, isComplete: complete))
            .Should().BeFalse();

    [Fact]
    public void ShouldUseMarkdown_CompleteText_ShouldBeTrue()
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateText("x", 0, isComplete: true))
            .Should().BeTrue();

    [Fact]
    public void ShouldUseMarkdown_StreamingReasoning_ShouldBeFalse()
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateReasoning("x", 0, isComplete: false))
            .Should().BeFalse();

    [Fact]
    public void ShouldUseMarkdown_CompleteReasoning_ShouldBeTrue()
        => RenderMarkdownPolicy.ShouldUseMarkdown(ContentBlock.CreateReasoning("x", 0, isComplete: true))
            .Should().BeTrue();

    [Fact]
    public void ShouldUseMarkdown_ToolCall_ShouldBeTrue()
        => RenderMarkdownPolicy.ShouldUseMarkdown(
                ContentBlock.CreateToolCall(new() { Id = "t1", Name = "bash", Status = "success" }, 0))
            .Should().BeTrue();

    [Fact]
    public void ShouldUseMarkdown_NullBlock_ShouldBeFalse()
        => RenderMarkdownPolicy.ShouldUseMarkdown(null!).Should().BeFalse();
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`RenderMarkdownPolicy` 未定义）。

- [ ] **Step 3: 实现策略类**

新建 `samples/Seeing.Agent.WebUI/Rendering/RenderMarkdownPolicy.cs`：

```csharp
using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// Markdown 渲染策略：流式进行中的文本/思考块跳过 Markdown，按纯文本渲染。
/// <para>
/// 原因：流式期每次增量都会让 Markdown 全文重解析，成本随文本长度平方增长
/// （<see cref="Caching.MemoryRenderCache.GetOrCreateMarkdown"/> 在内容变化时全量 ToHtml）。
/// 而流式阶段用户读的是增量，不需要即时排版。消息完成后一次性解析。
/// </para>
/// <para>非文本块（工具/附件/图片等）不受影响，恒走原有渲染路径。</para>
/// </summary>
public static class RenderMarkdownPolicy
{
    /// <summary>该内容块是否应走 Markdown 渲染。流式中的文本/思考/子代理块返回 <c>false</c>。</summary>
    public static bool ShouldUseMarkdown(ContentBlock block)
    {
        if (block is null)
            return false;

        return block.Type switch
        {
            ContentBlockType.Text or ContentBlockType.Reasoning or ContentBlockType.SubAgent
                => !block.IsStreaming,
            _ => true
        };
    }
}
```

> `ContentBlock.CreateText(content, sortIndex, isComplete)` 内部设 `IsStreaming = !isComplete`（`ContentBlock.cs:148-159`）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~StreamingRenderTests"`
Expected: 6 个测试 PASS。

- [ ] **Step 5: 补 CSS（不做这步会塌行）**

`.message-content` 当前只有 `line-height`/`word-wrap`（`wwwroot/css/message-item.css:268`），没有保留空白。追加：

```css
/* 流式中的纯文本：HTML 默认会折叠换行与连续空格，必须显式保留，
   否则流式期间的段落/列表/代码会挤成一整段 */
.message-content--streaming {
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-family: inherit;
    word-break: break-word;
}
```

- [ ] **Step 6: 接入两个 Renderer**

`TextBlockRenderer.Render` 改为：

```csharp
public RenderFragment Render(ContentBlock block, RenderContext context)
{
    var content = block.Content ?? string.Empty;

    if (!RenderMarkdownPolicy.ShouldUseMarkdown(block))
    {
        // 流式中：纯文本，避免全文 Markdown 重解析（block.IsStreaming 转 false 后会重新走下面的分支）
        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-text message-content message-content--streaming");
            builder.AddContent(2, content);
            builder.CloseElement();
        };
    }

    var cacheKey = context.GetMarkdownCacheKey(block);
    return builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "content-block-text message-content");
        var html = context.Cache?.GetOrCreateMarkdown(content, cacheKey) ?? content;
        builder.AddContent(2, new MarkupString(html));
        builder.CloseElement();
    };
}
```

`ReasoningBlockRenderer` 同样处理：流式时纯文本 + `message-content--streaming`，其余保留其现有折叠逻辑与 `ReasoningMessageComponent` 委托。

`Components/Messaging/TextMessageComponent.razor` 改为：

```razor
@using Seeing.Agent.WebUI.Models.Messaging
@using Seeing.Agent.WebUI.Rendering
@using Seeing.Agent.WebUI.Rendering.Abstractions

@* 文本消息组件 - 流式中纯文本（保留空白），完成后 Markdown *@

<div style="margin-left:15px" class="@(UseMarkdown ? "" : "message-content--streaming")">
    @if (UseMarkdown)
    {
        @((MarkupString)HtmlContent)
    }
    else
    {
        @Content
    }
</div>

@code {
    [Parameter] public string Content { get; set; } = string.Empty;
    [Parameter] public RenderContext Context { get; set; } = default!;
    [Parameter] public ContentBlock Block { get; set; } = default!;

    private bool UseMarkdown => Block is null || RenderMarkdownPolicy.ShouldUseMarkdown(Block);

    private string HtmlContent => string.IsNullOrEmpty(Content)
        ? string.Empty
        : Context.Cache?.GetOrCreateMarkdown(Content, Context.GetMarkdownCacheKey(Block)) ?? Content;
}
```

`ReasoningMessageComponent.razor` 按其现有结构同样接入 `RenderMarkdownPolicy`，保留展开/收起逻辑。

- [ ] **Step 7: 手工验证**

1. 长回答流式期：文本保留换行与缩进（不塌成一坨），无 Markdown 排版
2. 回答结束后**立即**变成完整 Markdown（标题、代码块、列表、表格）
3. 带思考的模型：思考块流式期纯文本，结束后格式化
4. 中途取消执行：已输出的文本仍可读
5. 刷新页面：历史消息全部为 Markdown 格式

- [ ] **Step 8: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Rendering/RenderMarkdownPolicy.cs samples/Seeing.Agent.WebUI/Rendering/Renderers/TextBlockRenderer.cs samples/Seeing.Agent.WebUI/Rendering/Renderers/ReasoningBlockRenderer.cs samples/Seeing.Agent.WebUI/Components/Messaging/TextMessageComponent.razor samples/Seeing.Agent.WebUI/Components/Messaging/ReasoningMessageComponent.razor samples/Seeing.Agent.WebUI/wwwroot/css/message-item.css tests/apps/Seeing.Agent.WebUI.Tests/Rendering/StreamingRenderTests.cs
git commit -m "perf(webui): 流式期间跳过 Markdown 全量解析，完成后一次性渲染"
```

---

## Task 4: Loop 分段渲染

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItem.cs:1-15`
- Modify: `samples/Seeing.Agent.WebUI/Models/MessageViewModel.cs`（新增 `Revision`/`Touch()`）
- Modify: `samples/Seeing.Agent.WebUI/Models/Messaging/ContentBlock.cs:332-374`
- Modify: `samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor:1-52`
- Modify: `samples/Seeing.Agent.WebUI/Components/TimelineItemView.razor:7`
- Modify: `samples/Seeing.Agent.WebUI/Rendering/MessageRenderPipeline.cs:119-275`
- Modify: `samples/Seeing.Agent.WebUI/Rendering/Abstractions/IMessageRenderPipeline.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs`（Touch 清单）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Models/ContentBlockBuilderTests.cs`（新建）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Models/TimelineRevisionTests.cs`（新建）

**Interfaces:**
- Consumes: 无（不依赖 Task 2/5）
- Produces:
  - `int TimelineItem.StructureRevision { get; }`、`void TimelineItem.TouchStructure()`
  - `int MessageViewModel.Revision { get; }`、`void MessageViewModel.Touch()`
  - `readonly record struct ContentBlockSegment(MessageViewModel Message, List<ContentBlock> Blocks)`
  - `static List<ContentBlockSegment> ContentBlockBuilder.BuildLoopSegments(IReadOnlyList<MessageViewModel> messages)`
  - `IMessageRenderPipeline` 新增：`RenderFragment RenderBlock(ContentBlock, RenderContext)`、`string GetLoopContainerClass(LoopGroupViewModel)`、`MessageStatus GetLoopStatus(LoopGroupViewModel)`、`List<MessageHeader.MessageTag> BuildLoopTags(LoopGroupViewModel)`、`List<MessageHeader.MessageMetaItem> BuildLoopMetaItems(LoopGroupViewModel)`

- [ ] **Step 1: 写失败测试**

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Models/ContentBlockBuilderTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Models;

/// <summary>分段内容块构建：Id 在 Loop 内唯一且跨次构建稳定。</summary>
public class ContentBlockBuilderTests
{
    private static MessageViewModel Assistant(string id, int step, string content, bool complete = true)
        => new() { Id = id, Role = "assistant", Step = step, Content = content, IsComplete = complete };

    [Fact]
    public void BuildLoopSegments_ShouldOrderByStep()
        => ContentBlockBuilder.BuildLoopSegments([
                Assistant("a2", 1, "第二步"), Assistant("a1", 0, "第一步")])
            .Select(s => s.Message.Id).Should().Equal("a1", "a2");

    [Fact]
    public void BuildLoopSegments_BlockIdsShouldBeUniqueAcrossMessages()
    {
        var ids = ContentBlockBuilder.BuildLoopSegments([
                Assistant("a1", 0, "第一步"), Assistant("a2", 1, "第二步")])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void BuildLoopSegments_DuplicateMessageId_BlockIdsShouldStillBeUnique()
    {
        var ids = ContentBlockBuilder.BuildLoopSegments([
                Assistant("same", 0, "一"), Assistant("same", 1, "二")])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void BuildLoopSegments_RepeatedCalls_BlockIdsShouldBeStable()
    {
        MessageViewModel[] input = [Assistant("a1", 0, "一"), Assistant("a2", 1, "二")];

        var first = ContentBlockBuilder.BuildLoopSegments(input).SelectMany(s => s.Blocks).Select(b => b.Id).ToList();
        var second = ContentBlockBuilder.BuildLoopSegments(input).SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        second.Should().Equal(first);
    }

    [Fact]
    public void BuildLoopSegments_StreamingToComplete_BlockIdsShouldStayStable()
    {
        var before = ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x", complete: false)])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();
        var after = ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x", complete: true)])
            .SelectMany(s => s.Blocks).Select(b => b.Id).ToList();

        after.Should().Equal(before);
    }

    [Fact]
    public void BuildLoopSegments_IncompleteMessage_TextBlockShouldBeStreaming()
        => ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x", complete: false)])
            .Single().Blocks.Should().Contain(b => b.Type == ContentBlockType.Text && b.IsStreaming);

    [Fact]
    public void BuildLoopSegments_CompleteMessage_ShouldHaveNoStreamingBlocks()
        => ContentBlockBuilder.BuildLoopSegments([Assistant("a1", 0, "x")])
            .Single().Blocks.Should().OnlyContain(b => !b.IsStreaming);
}
```

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Models/TimelineRevisionTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Timeline;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Models;

/// <summary>版本语义：结构变更与内容变更分别递增；VM 自身内容变更需单独 Touch。</summary>
public class TimelineRevisionTests
{
    [Fact]
    public void Touch_ShouldNotBumpStructureRevision()
    {
        var item = new TimelineItem { Key = "k", Kind = TimelineItemKind.AssistantTurn };
        item.Touch();

        item.Revision.Should().Be(1);
        item.StructureRevision.Should().Be(0);
    }

    [Fact]
    public void TouchStructure_ShouldBumpBoth()
    {
        var item = new TimelineItem { Key = "k", Kind = TimelineItemKind.AssistantTurn };
        item.TouchStructure();

        item.Revision.Should().Be(1);
        item.StructureRevision.Should().Be(1);
    }

    [Fact]
    public void MessageViewModel_Touch_ShouldBumpRevision()
    {
        var vm = new MessageViewModel { Id = "m1" };
        vm.Touch();

        vm.Revision.Should().Be(1);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`BuildLoopSegments`、`StructureRevision`、`MessageViewModel.Touch` 未定义）。

- [ ] **Step 3: 扩展版本字段**

`Models/Timeline/TimelineItem.cs`：

```csharp
namespace Seeing.Agent.WebUI.Models.Timeline;

public sealed class TimelineItem
{
    public required string Key { get; init; }
    public required TimelineItemKind Kind { get; init; }

    /// <summary>内容版本：任何可见变化都递增（驱动 TimelineItemView 重渲染）。</summary>
    public int Revision { get; private set; }

    /// <summary>结构版本：仅轮次内消息增删/重排时递增（驱动 LoopGroupItem 重建全部分段）。</summary>
    public int StructureRevision { get; private set; }

    public MessageViewModel? Message { get; internal set; }
    public LoopGroupViewModel? Turn { get; internal set; }

    /// <summary>标记内容变化（轮次结构不变）。</summary>
    public void Touch() => Revision++;

    /// <summary>标记结构变化（轮次内消息增删/重排），同时计入内容版本。</summary>
    public void TouchStructure()
    {
        Revision++;
        StructureRevision++;
    }

    public static string AssistantKey(string? loopId, string messageId)
        => string.IsNullOrEmpty(loopId) ? $"single-{messageId}" : loopId;
}
```

`Models/MessageViewModel.cs` 新增：

```csharp
/// <summary>视图模型版本：内容被就地更新时递增，供 LoopGroupItem 做分段缓存。</summary>
public int Revision { get; private set; }

/// <summary>标记内容变化。</summary>
public void Touch() => Revision++;
```

- [ ] **Step 4: 实现 BuildLoopSegments（保留旧方法）**

`Models/Messaging/ContentBlock.cs` 新增（**不删除** `BuildFromLoopMessages`，Task 4 全程保留它，Step 10 才删）：

```csharp
/// <summary>Loop 内单条消息的内容块段（用于分段缓存）。</summary>
public readonly record struct ContentBlockSegment(MessageViewModel Message, List<ContentBlock> Blocks);
```

`ContentBlockBuilder` 新增：

```csharp
/// <summary>从消息构建内容块，并为块 Id 加前缀以保证跨消息唯一。</summary>
public static List<ContentBlock> BuildFromMessage(MessageViewModel message, string? idPrefix)
{
    var blocks = BuildFromMessage(message);
    if (idPrefix is null)
        return blocks;

    for (var i = 0; i < blocks.Count; i++)
        blocks[i].Id = $"{idPrefix}:{blocks[i].Id}";

    return blocks;
}

/// <summary>
/// 按 Step 顺序构建 Loop 的分段内容块。
/// <para>块 Id 以消息 Id 为前缀，保证 Loop 内唯一且同一消息跨次构建稳定，
/// 从而渲染层可按消息粒度缓存，避免每次 delta 重建整个 Loop。</para>
/// <para>消息 Id 重复时（分叉复制/历史数据）追加 <c>#{n}</c> 后缀消歧；
/// 序号按 Step 排序后的出现次序确定，因此同一输入每次构建结果一致。</para>
/// </summary>
public static List<ContentBlockSegment> BuildLoopSegments(IReadOnlyList<MessageViewModel> messages)
{
    var ordered = messages.OrderBy(m => m.Step).ToList();
    var segments = new List<ContentBlockSegment>(ordered.Count);
    var seen = new Dictionary<string, int>(StringComparer.Ordinal);

    for (var i = 0; i < ordered.Count; i++)
    {
        var message = ordered[i];
        var prefix = string.IsNullOrEmpty(message.Id) ? $"anon-{i}" : message.Id;

        if (seen.TryGetValue(prefix, out var count))
        {
            seen[prefix] = count + 1;
            prefix = $"{prefix}#{count + 1}";
        }
        else
        {
            seen[prefix] = 0;
        }

        segments.Add(new ContentBlockSegment(message, BuildFromMessage(message, prefix)));
    }

    return segments;
}
```

- [ ] **Step 5: 运行构建器测试**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~ContentBlockBuilderTests|FullyQualifiedName~TimelineRevisionTests"`
Expected: 10 个测试 PASS。

- [ ] **Step 6: MessageTimelineStore 区分 Touch / TouchStructure / VM.Touch**

这是**最关键的一步**，漏掉任何一条都会导致流式结束后永久纯文本。

`Services/MessageTimelineStore.cs` 的改动清单：

| 位置 | 改动 |
|---|---|
| `CompleteTurn`（`:164-172`）循环内 | 改 `msg.IsComplete`/`IsReasoningComplete` **前后值比较**，值真变了才 `msg.Touch()`；循环外保留 `item.Touch()` |
| `SyncAssistantMessage` 已有消息分支（`:236-274`） | `changed == true` 时**在 `item.Touch()` 之前加 `existing.Touch()`** |
| `SyncAssistantMessage` turnComplete 分支（`:261-266`） | 若仅因 turnComplete 变化而 `changed`，也调 `existing.Touch()`（该 msg 无可见字段变化，安全） |
| `SyncAssistantMessage` 新增消息（`:212-219`） | `item.TouchStructure()`（新实例 Revision=0 无需 Touch） |
| `SyncAssistantMessage` 的 `item.Turn.IsComplete` 变化（`:229`） | `item.TouchStructure()` |
| `ResetFromSession` 合并既有 turn（`:118-124`） | `existingTurn.TouchStructure()`（新增了 message，结构变了） |
| `RekeyTurn` 合并分支（`:684`） | `existing.TouchStructure()`（消息并入，结构变了） |
| `RekeyTurn` 换 Key 分支（`:702`） | `rekeyed.TouchStructure()` |
| `AbsorbOrphanSinglesInto` 末尾（`:742-744`） | `target.TouchStructure()`，并把 `RenumberLoopIndexes()` 移出循环（Step 11 处理） |
| `ReconcileAppendFromSession` 折叠态变化（`:434-438`） | `existing.TouchStructure()`（条目内容/结构都变） |
| `ReconcileAppendFromSession` 新增条目（`:422-428`、`:465-470`） | 新实例，无需 Touch |

`CompleteTurn` 参考实现：

```csharp
public void CompleteTurn(string? loopId)
{
    var item = FindAssistantTurn(loopId, createIfMissing: false);
    if (item?.Turn == null)
        return;

    item.Turn.IsComplete = true;
    foreach (var msg in item.Turn.Messages)
    {
        if (!msg.IsComplete)
        {
            msg.IsComplete = true;
            // 消息自身的版本必须递增：LoopGroupItem 的分段缓存按 MessageViewModel.Revision 判定，
            // 不递增会复用流式期的纯文本片段，导致完成后永久停留未格式化状态
            msg.Touch();
        }

        if (!string.IsNullOrEmpty(msg.Reasoning) && !msg.IsReasoningComplete)
            msg.IsReasoningComplete = true;
    }

    item.Touch();
    RefreshTailHint();
    RaiseChanged();
}
```

- [ ] **Step 7: 补一条守门测试（防未来遗漏）**

在 `TimelineRevisionTests.cs` 追加：

```csharp
/// <summary>
/// Review Focus 守门：CompleteTurn 后每条消息的 Revision 必须递增，
/// 否则 LoopGroupItem 的分段缓存不会重建，流式纯文本不会被替换为 Markdown。
/// </summary>
[Fact]
public void CompleteTurn_ShouldBumpEveryMessageRevision()
{
    var store = new MessageTimelineStore();
    var msg = new SessionMessage
    {
        Id = "a1", Role = MessageRole.Assistant, LoopId = "loop1", Content = "内容", SessionId = "s1"
    };
    store.SyncAssistantMessage(msg, "s1");

    var before = store.Items[0].Turn!.Messages.Single().Revision;
    store.CompleteTurn("loop1");
    var after = store.Items[0].Turn!.Messages.Single().Revision;

    after.Should().BeGreaterThan(before);
    store.Items[0].Turn!.Messages.Single().IsComplete.Should().BeTrue();
}
```

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~TimelineRevisionTests"`
Expected: 4 个测试 PASS（该测试在 Step 6 完成前会 FAIL，这是预期的）。

- [ ] **Step 8: 扩展 IMessageRenderPipeline**

`Rendering/Abstractions/IMessageRenderPipeline.cs` 新增：

```csharp
/// <summary>渲染单个内容块（含工具后内联槽位），供 Loop 分段缓存复用。</summary>
RenderFragment RenderBlock(ContentBlock block, RenderContext context);

/// <summary>Loop 容器 CSS 类。</summary>
string GetLoopContainerClass(LoopGroupViewModel loop);

/// <summary>Loop 状态。</summary>
MessageStatus GetLoopStatus(LoopGroupViewModel loop);

/// <summary>Loop 标签（Loop #N / N 步骤）。</summary>
List<MessageHeader.MessageTag> BuildLoopTags(LoopGroupViewModel loop);

/// <summary>Loop 元信息项（工具数 / 耗时 / Token）。</summary>
List<MessageHeader.MessageMetaItem> BuildLoopMetaItems(LoopGroupViewModel loop);
```

需要 `using Seeing.Agent.WebUI.Components.Messaging;` 与 `using Seeing.Agent.WebUI.Models.Messaging;`。

`MessageRenderPipeline` 实现：把原静态 `BuildLoopTags`/`BuildLoopMetaItems` 重命名为 `BuildLoopTagsCore`/`BuildLoopMetaItemsCore`（避免 CS0111），实例方法转发；`GetLoopContainerClass`/`GetLoopStatus` 转发到 `GetLoopClass`/`DetermineLoopStatus`；`RenderBlock` 抽取自 `RenderContentBlocks`（`:216-275`）的单块体（含 `OpenRegion(block.SortIndex)`、`SetKey(block.Id)`、工具后槽位循环）。

`RenderLoop` 内部改用 `BuildLoopSegments` + 逐块 `RenderBlock`，避免两套实现漂移。

- [ ] **Step 9: LoopGroupItem 分段缓存**

`Components/LoopGroupItem.razor` 标记部分：

```razor
@using AntDesign
@using Seeing.Agent.WebUI.Components.Messaging
@using Seeing.Agent.WebUI.Models
@using Seeing.Agent.WebUI.Models.Messaging
@using Seeing.Agent.WebUI.Rendering.Abstractions
@inject IMessageRenderPipeline Pipeline

@* Loop 分组：头部每次重建（轻量），内容块按消息分段缓存 *@
<div class="@Pipeline.GetLoopContainerClass(Loop)">
    <MessageHeader Role="assistant"
                   Timestamp="@(Loop.StartTime ?? DateTime.Now)"
                   Status="@Pipeline.GetLoopStatus(Loop)"
                   ShowStatusIndicator="@Loop.IsExecuting"
                   Tags="@Pipeline.BuildLoopTags(Loop)"
                   MetaItems="@Pipeline.BuildLoopMetaItems(Loop)"
                   CustomRoleName="助手" />
    @_body
    @if (!string.IsNullOrEmpty(Loop.Error))
    {
        <div class="loop-error" style="padding: var(--space-2) var(--space-3); background: var(--color-error-bg); border: 1px solid var(--color-error-border); border-radius: var(--radius-sm); margin-top: var(--space-2); color: var(--color-error);">
            @Loop.Error
        </div>
    }
</div>
```

`@code`：

```csharp
[Parameter] public LoopGroupViewModel Loop { get; set; } = new();
[Parameter] public int ContentRevision { get; set; }
[Parameter] public int StructureRevision { get; set; }
[Parameter] public EventCallback<ToolCallViewModel> OnToolClick { get; set; }

private int _lastStructureRevision = int.MinValue;
private readonly List<SegmentCache> _segments = new();
private RenderFragment? _body;

/// <summary>单条消息的渲染缓存。只允许在 Dispatcher 线程读写。</summary>
private sealed class SegmentCache
{
    public required string MessageId { get; init; }
    public int Revision { get; set; }
    public required List<RenderFragment> Fragments { get; set; }
}

protected override void OnParametersSet()
{
    var ordered = Loop.Messages.OrderBy(m => m.Step).ToList();
    var structureChanged = _lastStructureRevision != StructureRevision
                           || _segments.Count != ordered.Count;

    if (structureChanged)
    {
        _segments.Clear();
        foreach (var message in ordered)
            _segments.Add(NewSegment(message));
    }
    else
    {
        // 结构未变：只重建版本变化的单条消息
        for (var i = 0; i < ordered.Count; i++)
        {
            if (_segments[i].MessageId != ordered[i].Id || _segments[i].Revision != ordered[i].Revision)
                _segments[i] = NewSegment(ordered[i]);
        }
    }

    _lastStructureRevision = StructureRevision;

    // 快照后再捕获：RenderFragment 只允许看到本次构建时的分段，
    // 避免后续 OnParametersSet 原地改 _segments 时旧片段读到新内容
    var snapshot = _segments.ToArray();
    _body = builder =>
    {
        var seq = 0;
        foreach (var segment in snapshot)
            foreach (var fragment in segment.Fragments)
                builder.AddContent(seq++, fragment);
    };
}

private SegmentCache NewSegment(MessageViewModel message)
{
    var context = Pipeline.BuildContext(message, new RenderOptions { ShowReasoning = true }, null, OnToolClick);
    var blocks = ContentBlockBuilder.BuildFromMessage(message, message.Id);

    return new SegmentCache
    {
        MessageId = message.Id,
        Revision = message.Revision,
        Fragments = blocks.OrderBy(b => b.SortIndex).Select(b => Pipeline.RenderBlock(b, context)).ToList()
    };
}
```

`Components/TimelineItemView.razor:7` 补参数：

```razor
<LoopGroupItem Loop="@Item.Turn"
               ContentRevision="@Item.Revision"
               StructureRevision="@Item.StructureRevision"
               OnToolClick="@OnToolClick" />
```

- [ ] **Step 10: 删除旧的 BuildFromLoopMessages**

确认 `grep BuildFromLoopMessages` 只命中 `ContentBlock.cs` 定义后，删除该方法与 `GetAdditionalKey`（`MessageRenderPipeline` 已在 Step 8 改用 `BuildLoopSegments`）。

- [ ] **Step 11: RenumberLoopIndexes 移出热路径**

`AbsorbOrphanSinglesInto` 改为：循环内只收集，循环外调一次 `RenumberLoopIndexes()`（原实现每次吸收后都调）。

- [ ] **Step 12: 验证**

Run: `dotnet build Seeing.Agent.slnx` → `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~ContentBlockBuilderTests|FullyQualifiedName~TimelineRevisionTests"`
Expected: 11 个测试 PASS。

手工验证（**必须全部通过**）：
1. 多步工具调用的长回答：每步的思考块、工具卡片、文本块顺序正确
2. 流式期工具卡片展开/收起状态不丢失
3. **流式结束后文本立即变为 Markdown 格式**（这是分段缓存 + VM.Touch 的核心验收点）
4. 回答结束后刷新页面，时间线结构与流式期一致
5. 同一 Loop 内有重复消息 Id 的历史数据（可用旧会话文件）不出现内容串位

- [ ] **Step 13: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItem.cs samples/Seeing.Agent.WebUI/Models/MessageViewModel.cs samples/Seeing.Agent.WebUI/Models/Messaging/ContentBlock.cs samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor samples/Seeing.Agent.WebUI/Components/TimelineItemView.razor samples/Seeing.Agent.WebUI/Rendering/MessageRenderPipeline.cs samples/Seeing.Agent.WebUI/Rendering/Abstractions/IMessageRenderPipeline.cs samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs tests/apps/Seeing.Agent.WebUI.Tests/Models/ContentBlockBuilderTests.cs tests/apps/Seeing.Agent.WebUI.Tests/Models/TimelineRevisionTests.cs
git commit -m "perf(webui): Loop 内按消息分段缓存渲染块，结构与内容版本分离"
```

---

## Task 5: MessageTimelineStore 索引化

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs:25-54, 56-156, 372-543, 549-781`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreIndexTests.cs`（新建）

**Interfaces:**
- Consumes: Task 4 的 `Touch`/`TouchStructure` 语义
- Produces: 公开 API 签名不变；内部新增 `_turnByKey`、`_itemByMessageId`、`_pendingOrphanKeys`（均 `private`）

- [ ] **Step 1: 写行为锁测试**

> **本任务无「失败测试」**：现有实现行为已正确，索引化是纯性能改造。以下测试是**行为锁**，Step 2 应全部 PASS，Step 3 改造后必须仍全部 PASS。

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreIndexTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>时间线索引化行为锁：同步、认领、幂等、折叠态均不得回归。</summary>
public class MessageTimelineStoreIndexTests
{
    private static SessionMessage Assistant(string id, string? loopId, string content, int step = 0)
        => new() { Id = id, Role = MessageRole.Assistant, LoopId = loopId, Step = step, Content = content, SessionId = "s1" };

    private static SessionMessage User(string id, string content)
        => new() { Id = id, Role = MessageRole.User, Content = content, SessionId = "s1" };

    [Fact]
    public void SyncAssistantMessage_NewLoop_ShouldCreateTurn()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", "loop1", "你好"), "s1");

        store.Items.Should().ContainSingle()
            .Which.Key.Should().Be("loop1");
        store.Items[0].Turn!.Messages.Should().ContainSingle();
    }

    [Fact]
    public void SyncAssistantMessage_SameLoopMoreSteps_ShouldAppendToSameTurn()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", "loop1", "一", 0), "s1");
        store.SyncAssistantMessage(Assistant("a2", "loop1", "二", 1), "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Turn!.Messages.Should().HaveCount(2);
    }

    [Fact]
    public void SyncAssistantMessage_OrphanThenLoopId_ShouldMergeIntoSingleTurn()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", null, "流式早期"), "s1");
        store.Items[0].Key.Should().StartWith("single-");

        store.SyncAssistantMessage(Assistant("a1", "loop1", "流式早期"), "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Key.Should().Be("loop1");
        store.Items[0].Turn!.LoopIndex.Should().Be(1);
    }

    [Fact]
    public void ResetFromSession_DuplicateId_ShouldKeepLast()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession([User("m1", "旧"), User("m1", "新")], "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Message!.Content.Should().Be("新");
    }

    [Fact]
    public void ReconcileAppendFromSession_ShouldBeIdempotent()
    {
        var store = new MessageTimelineStore();
        var messages = new List<SessionMessage> { User("m1", "一") };
        store.ResetFromSession(messages, "s1");

        messages.Add(User("m2", "二"));
        store.ReconcileAppendFromSession(messages, "s1");
        store.ReconcileAppendFromSession(messages, "s1");

        store.Items.Should().HaveCount(2);
    }

    [Fact]
    public void ReconcileAppendFromSession_ManyCallsSameCollection_ShouldNotGrow()
    {
        var store = new MessageTimelineStore();
        var messages = Enumerable.Range(0, 200).Select(i => User($"m{i}", $"内容{i}")).ToList();
        store.ResetFromSession(messages, "s1");

        for (var i = 0; i < 20; i++)
            store.ReconcileAppendFromSession(messages, "s1");

        store.Items.Should().HaveCount(200);
    }

    [Fact]
    public void SyncAssistantMessage_Unchanged_ShouldNotRaiseChanged()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", "loop1", "内容"), "s1");

        var raised = 0;
        store.Changed += () => raised++;
        store.SyncAssistantMessage(Assistant("a1", "loop1", "内容"), "s1");

        raised.Should().Be(0);
    }

    /// <summary>Review Focus：认领后索引不得与列表失步，否则消息会被重复追加。</summary>
    [Fact]
    public void AfterOrphanClaim_ReconcileShouldNotDuplicate()
    {
        var store = new MessageTimelineStore();
        var messages = new List<SessionMessage> { User("u1", "提问"), Assistant("a1", null, "早期") };
        store.ResetFromSession(messages, "s1");

        store.SyncAssistantMessage(Assistant("a1", "loop1", "早期"), "s1");
        messages[1] = Assistant("a1", "loop1", "早期");

        store.ReconcileAppendFromSession(messages, "s1");
        store.ReconcileAppendFromSession(messages, "s1");

        store.Items.Should().HaveCount(2);
        store.Items[0].Message!.Id.Should().Be("u1");
        store.Items[1].Key.Should().Be("loop1");
    }
}
```

- [ ] **Step 2: 运行确认全部通过（行为锁）**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests` → `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~MessageTimelineStoreIndexTests"`
Expected: 8 个测试 PASS。若有 FAIL，先修实现 bug 再继续（**不要改测试断言**）。

- [ ] **Step 3: 加索引**

新增字段：

```csharp
/// <summary>Assistant 轮次索引：turn key（loopId 或 single-*）→ 时间线条目。</summary>
private readonly Dictionary<string, TimelineItem> _turnByKey = new(StringComparer.Ordinal);

/// <summary>消息 Id → 所属条目（值为 null 表示排队消息，条目尚未建立）。</summary>
private readonly Dictionary<string, TimelineItem?> _itemByMessageId = new(StringComparer.Ordinal);

/// <summary>尚未被认领的 single-* 轮次 key（通常为空，避免每 delta 全表扫描）。</summary>
private readonly HashSet<string> _pendingOrphanKeys = new(StringComparer.Ordinal);
```

改造点（逐条对应现有行号）：

1. **`ResetFromSession`**（`:56-156`）：`_items.Clear()` 处同步清空三个索引；每个 `_items.Add` / `assistantByKey[key] = item` 后写 `_turnByKey[key] = item` 与 `_itemByMessageId[vm.Id] = item`；`_pending.Add(vm)` 后写 `_itemByMessageId[vm.Id] = null`；**key 以 `single-` 开头时写 `_pendingOrphanKeys.Add(key)`**（保证不变量「`_pendingOrphanKeys` ⊇ 所有 single-*」成立）。

2. **`FindTurnByKey`**（`:630-632`）：
```csharp
private TimelineItem? FindTurnByKey(string key) =>
    _turnByKey.TryGetValue(key, out var item) ? item : null;
```

3. **`FindItemForMessageId`**（`:485-501`）改为三态，避免「排队」与「不存在」混淆：
```csharp
/// <summary>按消息 Id 查找条目。<c>true</c> 表示该 Id 已知；<paramref name="item"/> 为 null 表示尚在排队。</summary>
private bool TryFindItemForMessageId(string id, out TimelineItem? item)
    => _itemByMessageId.TryGetValue(id, out item);
```
同步修改 `ReconcileAppendFromSession`（`:432-440`）的调用点。

4. **`CollectKnownMessageIds`**（`:517-543`）→ 返回 `_itemByMessageId.Keys` 的副本：
```csharp
private HashSet<string> CollectKnownMessageIds() =>
    new(_itemByMessageId.Keys, StringComparer.Ordinal);
```

5. **`CreateAssistantTurn`**（`:747-770`）：`_items.Add(item)` 后写 `_turnByKey[key] = item`；`seedVm` 非空时写 `_itemByMessageId[seedVm.Id] = item`；key 以 `single-` 开头时写 `_pendingOrphanKeys`。

6. **`SyncAssistantMessage`** 新增消息分支（`:213`）：`list.Add(incoming)` 后写 `_itemByMessageId[incoming.Id] = item`。

7. **`RekeyTurn`**（`:657-704`）：
   - 合并分支（`:672-690`）：`_items.RemoveAt(idx)` 后 `_turnByKey.Remove(old.Key)`、`_pendingOrphanKeys.Remove(old.Key)`，并把 `old.Turn.Messages` 的每个 Id 重新指向 `existing`
   - 换 Key 分支（`:692-703`）：`_items[idx] = rekeyed` 后 `_turnByKey.Remove(old.Key)`、`_turnByKey[loopId] = rekeyed`，并把消息 Id 指向 `rekeyed`

8. **`AbsorbOrphanSinglesInto`**（`:706-745`）→ 只遍历 `_pendingOrphanKeys`：
```csharp
private void AbsorbOrphanSinglesInto(TimelineItem target, string loopId, string currentMessageId)
{
    if (target.Turn == null)
        return;

    if (_pendingOrphanKeys.Count > 0)
    {
        foreach (var key in _pendingOrphanKeys.ToList())
        {
            if (!_turnByKey.TryGetValue(key, out var item))
            {
                _pendingOrphanKeys.Remove(key);
                continue;
            }
            if (ReferenceEquals(item, target) || item.Turn?.Messages == null)
            {
                if (ReferenceEquals(item, target))
                    _pendingOrphanKeys.Remove(key);
                continue;
            }

            // 仅吸收未完成、或消息已归属此 LoopId、或就是当前正在 sync 的 message
            var shouldAbsorb = !item.Turn.IsComplete
                || item.Turn.Messages.Any(m =>
                    string.Equals(m.LoopId, loopId, StringComparison.Ordinal)
                    || m.Id == currentMessageId);
            if (!shouldAbsorb)
                continue;

            foreach (var m in item.Turn.Messages)
            {
                if (target.Turn.Messages.All(x => x.Id != m.Id))
                    target.Turn.Messages.Add(m);
                _itemByMessageId[m.Id] = target;
            }

            var idx = _items.IndexOf(item);
            if (idx >= 0)
                _items.RemoveAt(idx);
            _turnByKey.Remove(key);
            _pendingOrphanKeys.Remove(key);
        }
    }

    target.Turn.LoopId = loopId;
    target.Turn.IsComplete = target.Turn.Messages.All(m => m.IsComplete);
    _turnByKey[loopId] = target;
    _pendingOrphanKeys.Remove(loopId);
    RenumberLoopIndexes();   // 移出循环，只调一次
    target.TouchStructure();
}
```

9. **`ReconcileAppendFromSession`**（`:410-473`）：`_pending.RemoveAt(...)` 与 `_items.Add(...)` 后写 `_itemByMessageId[pendingVm.Id] = newItem`；`_pending.Add(vm)` 后写 `_itemByMessageId[vm.Id] = null`。

10. **空 Id 归一化**（消除索引语义不稳）：`ResetFromSession` 与 `ReconcileAppendFromSession` 遍历消息时，若 `string.IsNullOrEmpty(msg.Id)` 则先补 `msg.Id = Guid.NewGuid().ToString("N")[..12]`（与 `:404-405` 现有行为一致），保证 `vm.Id == msg.Id` 恒成立。

- [ ] **Step 4: 运行确认行为锁仍全部通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~MessageTimelineStoreIndexTests"`
Expected: 8 个测试全 PASS。任何一条 FAIL 都说明索引维护点漏了。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreIndexTests.cs
git commit -m "perf(webui): 时间线索引化，去掉每 delta 全表扫描"
```

---

## Task 6: RenderCoalescer 渲染合并

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/RenderCoalescer.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/RenderCoalescerTests.cs`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`（`_requestRender` 回调、`OnHandlerChanged`、`OnAggregatorAssistantChanged`、`Dispose`）
- Modify: `samples/Seeing.Agent.WebUI/Components/MessageList.razor:84-123`

**Interfaces:**
- Produces:
  - `sealed class RenderCoalescer : IDisposable`
  - `RenderCoalescer(Action render, TimeSpan window, bool flushOnDispose = false)`
  - `void Request()`、`Task FlushAsync()`、`int RequestCount`、`int RenderCount`

**定位说明**：本任务**不是**「3 次渲染合并为 1 次」（该前提已被证伪）。价值是：① 高频 delta（>30/s）时封顶渲染速率；② 压平多个窗口/事件回放同时触发的渲染爆发；③ 把渲染从同步栈上推迟，让同批事件先处理完。

- [ ] **Step 1: 写失败测试**

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/RenderCoalescerTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>渲染合并器：窗口内合并、尾随执行、异常不外溢、释放语义。</summary>
public class RenderCoalescerTests
{
    [Fact]
    public async Task Request_MultipleWithinWindow_ShouldRenderOnce()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromMilliseconds(40));

        for (var i = 0; i < 10; i++) c.Request();
        await Task.Delay(250);

        renders.Should().Be(1);
        c.RequestCount.Should().Be(10);
    }

    [Fact]
    public async Task Request_AfterWindowElapsed_ShouldRenderAgain()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromMilliseconds(30));

        c.Request();
        await Task.Delay(150);
        c.Request();
        await Task.Delay(150);

        renders.Should().Be(2);
    }

    [Fact]
    public async Task FlushAsync_WithPending_ShouldRenderImmediately()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10));

        c.Request();
        await c.FlushAsync();

        renders.Should().Be(1);
    }

    [Fact]
    public async Task FlushAsync_WithoutPending_ShouldNotRender()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10));

        await c.FlushAsync();

        renders.Should().Be(0);
    }

    /// <summary>Review Focus： Dispose 默认不尾随渲染（组件销毁后渲染无意义且可能抛错）。</summary>
    [Fact]
    public async Task Dispose_WithoutFlushOption_ShouldNotRender()
    {
        var renders = 0;
        var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10));

        c.Request();
        c.Dispose();
        await Task.Delay(120);

        renders.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_WithFlushOption_ShouldRenderTrailing()
    {
        var renders = 0;
        var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10), flushOnDispose: true);

        c.Request();
        c.Dispose();
        await Task.Delay(120);

        renders.Should().Be(1);
    }

    [Fact]
    public async Task Request_AfterDispose_ShouldBeIgnored()
    {
        var renders = 0;
        var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromMilliseconds(20));

        c.Dispose();
        c.Request();
        await Task.Delay(120);

        renders.Should().Be(0);
    }

    /// <summary>Review Focus：渲染回调抛异常不得逃逸到 Timer 线程（会终止进程）。</summary>
    [Fact]
    public async Task Request_WhenRenderThrows_ShouldNotCrashProcess()
    {
        using var c = new RenderCoalescer(() => throw new InvalidOperationException("模拟渲染异常"),
            TimeSpan.FromMilliseconds(20));

        c.Request();
        await Task.Delay(150);

        c.RenderCount.Should().Be(1);  // 已尝试渲染，异常被吞
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`RenderCoalescer` 未定义）。

- [ ] **Step 3: 实现 RenderCoalescer**

新建 `samples/Seeing.Agent.WebUI/Services/RenderCoalescer.cs`：

```csharp
namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 渲染请求合并器：窗口期内的多次 <see cref="Request"/> 合并为一次回调，并在窗口结束时尾随执行。
/// <para>
/// 用途：事件流每个 delta 都会请求 UI 刷新。经本类合并后，渲染次数从「与 delta 次数线性相关」
/// 降为「与时间窗口数线性相关」，从而在高频流式输出时封顶渲染速率。
/// </para>
/// <para>
/// 线程契约：<see cref="Request"/> 可在任意线程调用；回调由定时器线程触发，
/// <b>调用方必须自行切回 UI 线程</b>，并确保回调内异常不外溢（本类已兜底，但回调本身仍应 try/catch）。
/// </para>
/// </summary>
public sealed class RenderCoalescer : IDisposable
{
    private readonly Action _render;
    private readonly TimeSpan _window;
    private readonly bool _flushOnDispose;
    private readonly object _gate = new();

    private Timer? _timer;
    private bool _pending;
    private bool _disposed;
    private int _requestCount;
    private int _renderCount;

    public RenderCoalescer(Action render, TimeSpan window, bool flushOnDispose = false)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _window = window;
        _flushOnDispose = flushOnDispose;
    }

    /// <summary>累计请求次数（诊断用）。</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>累计实际渲染次数（诊断用）。</summary>
    public int RenderCount => Volatile.Read(ref _renderCount);

    /// <summary>请求一次渲染；窗口期内重复调用只会在窗口结束时渲染一次。</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            Interlocked.Increment(ref _requestCount);
            _pending = true;
            _timer ??= new Timer(OnTimerElapsed, null, _window, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>立即执行待处理请求（无待处理时直接返回）。</summary>
    public Task FlushAsync()
    {
        RenderIfPending();
        return Task.CompletedTask;
    }

    private void OnTimerElapsed(object? state)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        RenderIfPending();
    }

    private void RenderIfPending()
    {
        lock (_gate)
        {
            if (_disposed || !_pending)
                return;

            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }

        Interlocked.Increment(ref _renderCount);
        try
        {
            _render();
        }
        catch (Exception ex)
        {
            // 定时器线程上的未捕获异常会终止进程；渲染失败不应影响宿主存活
            System.Diagnostics.Debug.WriteLine($"[RenderCoalescer] 渲染回调异常: {ex}");
        }
    }

    /// <summary>
    /// 释放。默认<b>不</b>尾随渲染：组件销毁后这一次渲染没有观察者，
    /// 且在 circuit 已关闭时调用 <c>InvokeAsync(StateHasChanged)</c> 会抛异常。
    /// </summary>
    public void Dispose()
    {
        bool trailing;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            trailing = _flushOnDispose && _pending;
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }

        if (trailing)
        {
            Interlocked.Increment(ref _renderCount);
            try { _render(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RenderCoalescer] 尾随渲染异常: {ex}"); }
        }
    }
}
```

> `OnTimerElapsed` 与 `RenderIfPending` 都触及 `_timer` 的注释：`RenderIfPending` 里 Dispose 掉刚被 `Request` 新建的 timer 是**安全**的 —— 那次 Request 的渲染需求由紧随的 `_render()` 立即满足，丢的只是窗口延迟。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~RenderCoalescerTests"`
Expected: 8 个测试 PASS。

- [ ] **Step 5: 接入 SessionWindow（仅内容刷新路径）**

1. 新增字段：

```csharp
private bool _disposed;

/// <summary>渲染合并器：仅用于「消息内容刷新」路径。用户交互路径保持即时渲染，避免受控组件回滚。</summary>
private Services.RenderCoalescer? _renderCoalescer;
```

2. `OnInitializedAsync` 开头创建：

```csharp
_renderCoalescer = new Services.RenderCoalescer(
    () => SafeRender(),
    TimeSpan.FromMilliseconds(33));
```

3. 新增安全渲染方法：

```csharp
/// <summary>从任意线程安全地请求一次渲染。组件已销毁或 circuit 断开时静默跳过。</summary>
private void SafeRender()
{
    if (_disposed)
        return;

    try
    {
        _ = InvokeAsync(StateHasChanged);
    }
    catch (Exception ex)
    {
        // circuit 已断开：渲染无接收方，不应影响宿主
        System.Diagnostics.Debug.WriteLine($"[SessionWindow] 渲染跳过: {ex.Message}");
    }
}
```

4. **改动点（严格限定这 3 处）**：
   - `EnsureSyncForSession` 的 `requestRender` 回调（`:643`）：`() => _renderCoalescer?.Request()`
   - `OnHandlerChanged` 末尾（`:1052`）的 `StateHasChanged();`：`_renderCoalescer?.Request();`
   - `OnAggregatorAssistantChanged`（`:1121`）：`StateHasChanged();` → `_renderCoalescer?.Request();`

5. **不改 `SyncContextState()` 末尾（`:1164`）的渲染**：该路径承载受控组件（Agent/Model Select）的值同步，代码里已有注释说明必须即时（`:1223-1226`、`:1238-1239` 的「先同步 Context 再异步保存，避免 AntDesign Select 回滚」）。延迟 33ms 会放大回滚窗口。

6. `Dispose()` 中置位并释放：

```csharp
_disposed = true;
_renderCoalescer?.Dispose();   // 默认不尾随渲染
_renderCoalescer = null;
```

放在 `Dispose()` **最开头**（早于 `SessionState.CancelExecution()` / `SessionState.Dispose()`）。

- [ ] **Step 6: MessageList 保留自驱动（不做 SelfRender=false）**

**不引入 `SelfRender` 参数**。原因：父级传给 `MessageList` 的参数（`Timeline` 同实例、`AutoScroll` 常量、`OnToolClick` 同委托、三个 `Compaction*` 流式期不变）在流式期间全部相等，Blazor 的 `ParameterView.ChangeDetection.MayHaveChanged` 会跳过 `SetParametersAsync`，导致 `OnAfterRenderAsync` 不执行、流式内容不推进。

改为把 `MessageList` 的自渲染也纳入合并器（同一窗口内合并，不会额外增加渲染次数）：

```csharp
private void OnTimelineChanged()
{
    SyncWindowFromStore(forcePinOnGenerationBump: true);
    _ = InvokeAsync(StateHasChanged);
}
```

保持不变。仅在 `SessionWindow` 传入的渲染密集时，两者会落在同一个 33ms 窗口内由 Blazor 合并成一次渲染通道。

> 若实测发现 `MessageList` 的渲染在流式期仍然过密，再单独评估：给它加 `StoreRevision` 参数（`Timeline.Generation * 1_000_000 + Timeline.TailRevision`）作为变化信号，并把 `SelfRender` 设为 `false`。

- [ ] **Step 7: 验证**

Run: `dotnet build Seeing.Agent.slnx`

手工验证：
1. 长回答流式输出流畅，无逐字卡顿
2. 回答结束后内容是完整 Markdown
3. **切换 Agent / Model 时 Select 不回滚**（这是 coalescer 不应触及的路径）
4. 切换会话后立即操作工具栏，响应即时（不被 33ms 延迟感知到）
5. 导航离开页面 / 刷新：控制台无 `TaskCanceledException` / `ObjectDisposedException` / 进程崩溃

- [ ] **Step 8: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/RenderCoalescer.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/RenderCoalescerTests.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor
git commit -m "perf(webui): 引入渲染合并器，封顶高频 delta 的渲染速率"
```

---

## Task 7: 多 agent 热路径

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/SerialGate.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SerialGateTests.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs:30, 64-76, 120-204`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:1110-1123`

**Interfaces:**
- Produces:
  - `sealed class SerialGate` + `Scope Enter()`（`using` 作用域）
  - `TaskCardAggregator` 的 `_writeLock`（`SemaphoreSlim`）替换为 `_writeGate`（`SerialGate`）

- [ ] **Step 1: 写失败测试**

`TaskCardAggregator` 构造依赖 `SessionEventStreamRouter`（Singleton，需 `ISessionRuntime` + `IServiceScopeFactory`），直接构造代价过高。本任务把锁语义抽成可单测的小类。

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/SerialGateTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>串行闸：并发进入时互斥、异常后可继续使用、可重入。</summary>
public class SerialGateTests
{
    [Fact]
    public void Enter_ShouldSerializeConcurrentWriters()
    {
        var gate = new SerialGate();
        var concurrent = 0;
        var maxConcurrent = 0;
        var completed = 0;

        Parallel.For(0, 200, _ =>
        {
            using (gate.Enter())
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                Thread.SpinWait(2000);
                Interlocked.Decrement(ref concurrent);
                Interlocked.Increment(ref completed);
            }
        });

        completed.Should().Be(200);
        maxConcurrent.Should().Be(1);
    }

    [Fact]
    public void Enter_AfterException_ShouldStillBeUsable()
    {
        var gate = new SerialGate();

        var act = () => { using (gate.Enter()) throw new InvalidOperationException("模拟异常"); };
        act.Should().Throw<InvalidOperationException>();

        using var scope = gate.Enter();
        scope.Should().NotBeNull();
    }

    [Fact]
    public void Enter_NestedOnSameThread_ShouldBeReentrant()
    {
        // Monitor 可重入（SemaphoreSlim 不可），聚合器内可能有嵌套调用
        var gate = new SerialGate();

        using (gate.Enter())
        {
            var act = () => { using (gate.Enter()) { } };
            act.Should().NotThrow();
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`SerialGate` 未定义）。

- [ ] **Step 3: 实现 SerialGate**

新建 `samples/Seeing.Agent.WebUI/Services/SerialGate.cs`：

```csharp
namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 轻量串行闸：用 <see cref="Monitor"/> 实现的互斥作用域。
/// <para>
/// 替代 <c>SemaphoreSlim.Wait()</c> 的同步等待：临界区为纯内存读-改-写（微秒级），
/// 用 Monitor 省掉 <see cref="SemaphoreSlim"/> 的异步等待状态机与内核对象开销。
/// </para>
/// <para>
/// 注意：Monitor 可重入，<see cref="SemaphoreSlim"/> 不可重入。这是有意的语义变更 ——
/// 聚合器内存在嵌套调用同一临界区的可能，可重入避免自死锁。
/// </para>
/// </summary>
public sealed class SerialGate
{
    private readonly object _gate = new();

    /// <summary>进入临界区；<c>using</c> 结束时自动释放。</summary>
    public Scope Enter()
    {
        Monitor.Enter(_gate);
        return new Scope(_gate);
    }

    /// <summary>临界区作用域。</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly object? _gate;

        internal Scope(object gate) => _gate = gate;

        public void Dispose()
        {
            if (_gate is not null)
                Monitor.Exit(_gate);
        }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~SerialGateTests"`
Expected: 3 个测试 PASS。

- [ ] **Step 5: 接入 TaskCardAggregator**

1. `:30`：`private readonly SemaphoreSlim _writeLock = new(1, 1);` → `private readonly SerialGate _writeGate = new();`
   **保留 `_saveLock`**（`:31`）—— 它保护异步落盘，仍需 `SemaphoreSlim`。
2. `Rebind`（`:64-72`）、`OnEvent`（`:120-139`）、`TryMountTask`（`:187-203`）、`MergeStep`/`FailStep` 的
   `_writeLock.Wait(); try { ... } finally { _writeLock.Release(); }`
   → `using (_writeGate.Enter()) { ... }`
3. 确认所有 `using` 块内**没有 `await`**（`AttachFromToolCallAsync` 的 `await` 在锁外，`OnEvent` 里 `AssistantChanged` 已在锁外触发）。
4. `Dispose` 里若有 `_writeLock.Dispose()`，删除。

- [ ] **Step 6: 聚合器回调节流**

`SessionWindow.OnAggregatorAssistantChanged`（`:1110-1123`）当前每个子会话事件都 `Timeline.SyncAssistantMessage` + 渲染。多子会话并发时是主要放大源。

改为用 coalescer 统一驱动（`SyncAssistantMessage` 本身幂等，重复调用不会产生额外可见变化）：

```csharp
private void OnAggregatorAssistantChanged(SessionMessage msg)
{
    var aggId = ResolveAggregatorSessionId();
    if (!string.Equals(EffectiveSessionId, aggId, StringComparison.Ordinal))
        return;

    // 聚合器回调来自子会话事件流后台线程；时间线变更必须调度回 Blazor 同步上下文。
    // 经渲染合并器统一驱动：多子会话并发时不再每次事件都触发一次渲染。
    _ = InvokeAsync(() =>
    {
        Timeline.SyncAssistantMessage(msg, EffectiveSessionId);
        _renderCoalescer?.Request();
    });
}
```

> 注意仍要 `InvokeAsync`（线程契约），但渲染请求走合并器。

- [ ] **Step 7: EventStreamHandler 加防乱序断言注释**

`Services/EventStreamHandler.cs:765-775` 的 `OnEvent` 是 fire-and-forget。当前安全（`ProcessEventAsync` 无 `await`）。加注释锁住这个前提：

```csharp
/// <summary>
/// 事件流消费者入口（Router 广播回调）。fire-and-forget 处理事件。
/// <para>
/// <b>不变量：<see cref="ProcessEventAsync"/> 必须全程同步完成（不得出现 await）。</b>
/// 当前实现满足此前提，因此事件按广播顺序处理、不会乱序也不会堆积 Task。
/// 若将来需要引入 await，必须改为串行队列消费，否则会出现事件乱序与无界并发。
/// </para>
/// </summary>
public void OnEvent(IMessageEvent evt)
```

- [ ] **Step 8: 用 TryGetMessage 优化会话引用重绑**

需 Task 9 完成后执行（若 Task 9 尚未做，本步跳过并在 PR 中标注）。

`EventStreamHandler.EnsureCurrentSessionRef`（`:317-333`）的 `cached.Messages.FirstOrDefault(m => ...)` 改为：

```csharp
if (_currentAssistantMessage != null &&
    !string.IsNullOrEmpty(_currentAssistantMessage.Id) &&
    cached.TryGetMessage(_currentAssistantMessage.Id, out var same) &&
    string.Equals(same.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
    !ReferenceEquals(same, _currentAssistantMessage))
{
    _currentAssistantMessage = same;
}
```

`HandleToolCall`（`:443-449`）的 `Where(...).LastOrDefault()` 改为倒序短路：

```csharp
var session = _sessionManager.Get(SessionId);
if (session?.Messages is { Count: > 0 } messages)
{
    for (var i = messages.Count - 1; i >= 0; i--)
    {
        if (string.Equals(messages[i].Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            _currentAssistantMessage = messages[i];
            break;
        }
    }
}
```

- [ ] **Step 9: 验证**

Run: `dotnet build Seeing.Agent.slnx`

手工验证：
1. 触发并行 3 个子会话的任务，三者同时流式时主区 UI 不卡
2. Task 卡步骤实时更新（不丢、不重）
3. 任务结束后刷新，TaskSteps 完整（落盘防抖未丢）
4. 侧栏 3 个 Summary 卡片状态正确更新

- [ ] **Step 10: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SerialGate.cs samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/SerialGateTests.cs
git commit -m "perf(webui): 聚合器改 SerialGate、回调经渲染合并器节流"
```

---

## Task 8: 滚动 JS 互操作限流

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/ScrollInteropThrottle.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/ScrollInteropThrottleTests.cs`
- Modify: `samples/Seeing.Agent.WebUI/Components/MessageList.razor:164-304`

**Interfaces:**
- Produces: `sealed class ScrollInteropThrottle`、`ScrollInteropThrottle(TimeSpan minInterval)`、`bool ShouldInvoke(DateTime now)`、`void Reset()`

- [ ] **Step 1: 写失败测试**

新建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/ScrollInteropThrottleTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>滚动互操作限流：窗口内只放行一次。</summary>
public class ScrollInteropThrottleTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 10, 0, 0);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    [Fact]
    public void ShouldInvoke_FirstCall_ShouldReturnTrue()
        => new ScrollInteropThrottle(Interval).ShouldInvoke(T0).Should().BeTrue();

    [Fact]
    public void ShouldInvoke_WithinInterval_ShouldReturnFalse()
    {
        var t = new ScrollInteropThrottle(Interval);
        t.ShouldInvoke(T0);
        t.ShouldInvoke(T0.AddMilliseconds(20)).Should().BeFalse();
    }

    [Fact]
    public void ShouldInvoke_AfterInterval_ShouldReturnTrue()
    {
        var t = new ScrollInteropThrottle(Interval);
        t.ShouldInvoke(T0);
        t.ShouldInvoke(T0.AddMilliseconds(41)).Should().BeTrue();
    }

    [Fact]
    public void Reset_ShouldAllowImmediateInvoke()
    {
        var t = new ScrollInteropThrottle(Interval);
        t.ShouldInvoke(T0);
        t.Reset();
        t.ShouldInvoke(T0.AddMilliseconds(1)).Should().BeTrue();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`ScrollInteropThrottle` 未定义）。

- [ ] **Step 3: 实现限流器**

新建 `samples/Seeing.Agent.WebUI/Services/ScrollInteropThrottle.cs`：

```csharp
namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 滚动互操作限流器：限制相邻两次 JS 互操作的最小间隔。
/// <para>
/// 消息列表每次渲染后的滚动判定都要往返 JS；与高频渲染叠加时会形成「渲染—往返」串行排队，
/// 表现为流式输出发涩。经限流后 JS 调用次数与渲染次数解耦。
/// </para>
/// <para>非线程安全：调用方保证在 Blazor Dispatcher 线程使用。</para>
/// </summary>
public sealed class ScrollInteropThrottle
{
    private readonly TimeSpan _minInterval;
    private DateTime? _lastInvokedAt;

    public ScrollInteropThrottle(TimeSpan minInterval) => _minInterval = minInterval;

    /// <summary>若距上次放行已超过最小间隔则放行并记录，否则返回 <c>false</c>。</summary>
    public bool ShouldInvoke(DateTime now)
    {
        if (_lastInvokedAt is DateTime last && now - last < _minInterval)
            return false;

        _lastInvokedAt = now;
        return true;
    }

    /// <summary>重置限流窗口（强制贴底、会话切换等需要立即判定的场景）。</summary>
    public void Reset() => _lastInvokedAt = null;
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~ScrollInteropThrottleTests"`
Expected: 4 个测试 PASS。

- [ ] **Step 5: 接入 MessageList（含限流跳过时的补偿）**

1. 新增字段：

```csharp
/// <summary>滚动 JS 往返限流（40ms）：与高频渲染解耦，避免「渲染—往返」串行排队。</summary>
private readonly Services.ScrollInteropThrottle _scrollThrottle = new(TimeSpan.FromMilliseconds(40));

/// <summary>限流跳过时置位：下次渲染必须补偿一次滚动判定，否则内容增长后不再贴底。</summary>
private bool _scrollDirty;
```

2. `OnAfterRenderAsync` 的 `contentGrew` 分支（`:237-259`）改为：

```csharp
if (contentGrew)
    _scrollDirty = true;

// 强制贴底与 prepend 恢复必须立即执行；常规增长限流
var mustScroll = _pendingForcePin
                 || _pendingPrependHeight.HasValue
                 || _scrollThrottle.ShouldInvoke(DateTime.UtcNow);

if (_scrollDirty && mustScroll)
{
    _scrollDirty = false;
    try
    {
        var pinned = await JSRuntime.InvokeAsync<bool>("shouldAutoScroll");
        if (pinned)
        {
            var slid = MessageListWindow.SlideWhilePinned(_windowStart, items.Count);
            if (slid != _windowStart)
            {
                _windowStart = slid;
                await InvokeAsync(StateHasChanged);
            }
        }

        if (AutoScroll)
            await JSRuntime.InvokeVoidAsync("notifyContentGrew");
    }
    catch
    {
        // 滚动为 UI 增强：JS 不可用时不阻断渲染
    }
}
```

> **关键**：`_trackedRenderCount/_trackedTailKey/_trackedTailRevision` 的更新仍在 `contentGrew` 计算之后、分支之前（原位置 `:233-235`），但 `_scrollDirty` 保证「限流跳过的那次增长」不会被永久遗忘。

3. `TryLoadMoreAsync`（`:282-304`）的 `isNearTop` 往返也纳入限流（长会话 `_windowStart > 0` 恒真，每次渲染都会往返一次）：

```csharp
private async Task TryLoadMoreAsync()
{
    if (_loadMoreInProgress || _pendingPrependHeight.HasValue || _windowStart <= 0)
        return;

    if (!_scrollThrottle.ShouldInvoke(DateTime.UtcNow))
        return;   // 与贴底判定共享同一个窗口，避免额外往返

    try
    {
        var nearTop = await JSRuntime.InvokeAsync<bool>("isNearTop");
        if (!nearTop)
            return;

        _loadMoreInProgress = true;
        var prevHeight = await JSRuntime.InvokeAsync<double>("getScrollHeight");
        _windowStart = MessageListWindow.LoadMore(_windowStart);
        _pendingPrependHeight = prevHeight;
        await InvokeAsync(StateHasChanged);
    }
    catch
    {
        _pendingPrependHeight = null;
        _loadMoreInProgress = false;
    }
}
```

4. `OnPinRequested` 中加 `_scrollThrottle.Reset(); _scrollDirty = true;`，保证强制贴底不被打断。

- [ ] **Step 6: 验证**

Run: `dotnet build Seeing.Agent.slnx`

手工验证：
1. 长会话流式输出时向上滚动：能停住不动，不被拉回
2. 滚回底部：自动恢复贴底，且**不落后几行**（这是 `_scrollDirty` 的验收点）
3. 滚到顶部：懒加载触发，位置不跳动
4. DevTools 里流式期间 JS 任务数明显少于改造前

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/ScrollInteropThrottle.cs samples/Seeing.Agent.WebUI/Components/MessageList.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/ScrollInteropThrottleTests.cs
git commit -m "perf(webui): 滚动互操作限流，解除渲染与 JS 往返的串行耦合"
```

---

## Task 9: SessionData 消息快照缓存与 Id 索引

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/SessionData.cs:98-157, 215-225, 255-395`
- Test: `tests/primitives/Seeing.Session.Tests/SessionMessageSnapshotTests.cs`（新建）

**Interfaces:**
- Produces:
  - `SessionData.Messages` 签名不变（`IReadOnlyList<SessionMessage>`），语义变为「同版本返回同一实例」
  - `bool SessionData.TryGetMessage(string? messageId, out SessionMessage message)`

- [ ] **Step 1: 写失败测试**

新建 `tests/primitives/Seeing.Session.Tests/SessionMessageSnapshotTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Session.Core;

namespace Seeing.Session.Tests;

/// <summary>消息快照缓存：同版本复用实例、写入后失效、Id 索引可查。</summary>
public class SessionMessageSnapshotTests
{
    private static SessionData NewSession() => SessionData.Create();

    [Fact]
    public void Messages_WithoutMutation_ShouldReturnSameInstance()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" });

        s.Messages.Should().BeSameAs(s.Messages);
    }

    [Fact]
    public void Messages_AfterAdd_ShouldInvalidate()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" });
        var before = s.Messages;

        s.AddMessage(new SessionMessage { Id = "m2", Role = MessageRole.Assistant, Content = "二" });

        s.Messages.Should().NotBeSameAs(before);
        s.Messages.Should().HaveCount(2);
    }

    [Fact]
    public void Messages_AfterRemove_ShouldReflectRemoval()
    {
        var s = NewSession();
        var msg = new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" };
        s.AddMessage(msg);
        _ = s.Messages;

        s.RemoveMessage(msg);

        s.Messages.Should().BeEmpty();
    }

    [Fact]
    public void TryGetMessage_WhenExists_ShouldReturnMessage()
    {
        var s = NewSession();
        var msg = new SessionMessage { Id = "abc", Role = MessageRole.User, Content = "内容" };
        s.AddMessage(msg);

        s.TryGetMessage("abc", out var found).Should().BeTrue();
        found.Should().BeSameAs(msg);
    }

    [Fact]
    public void TryGetMessage_WhenMissing_ShouldReturnFalse()
        => NewSession().TryGetMessage("nope", out _).Should().BeFalse();

    [Fact]
    public void TryGetMessage_WhenIdNull_ShouldReturnFalse()
        => NewSession().TryGetMessage(null, out _).Should().BeFalse();

    [Fact]
    public void TryGetMessage_AfterRemove_ShouldReturnFalse()
    {
        var s = NewSession();
        var msg = new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" };
        s.AddMessage(msg);

        s.RemoveMessage(msg);

        s.TryGetMessage("m1", out _).Should().BeFalse();
    }

    [Fact]
    public void GetActiveMessages_WithSummary_ShouldStartAtLastSummary()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "old", Role = MessageRole.User, Content = "旧" });
        s.AddMessage(new SessionMessage { Id = "sum", Role = MessageRole.Assistant, Content = "摘要", IsSummary = true });
        s.AddMessage(new SessionMessage { Id = "new", Role = MessageRole.User, Content = "新" });

        var active = s.GetActiveMessages();

        active.Should().HaveCount(2);
        active[0].Id.Should().Be("sum");
    }

    /// <summary>Review Focus：GetActiveMessages 返回的必须是独立副本，调用方修改不得污染会话真相。</summary>
    [Fact]
    public void GetActiveMessages_ReturnedList_ShouldBeIndependent()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" });

        var active = s.GetActiveMessages();
        active.Clear();

        s.Messages.Should().HaveCount(1);
        s.MessageCount.Should().Be(1);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/primitives/Seeing.Session.Tests` → `dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionMessageSnapshotTests"`
Expected: 编译失败（`TryGetMessage` 未定义）。

- [ ] **Step 3: 实现快照缓存与索引**

`SessionData` 改动：

1. 新增字段（`_messagesGate` 旁）：

```csharp
private IReadOnlyList<SessionMessage>? _messagesSnapshot;
private Dictionary<string, SessionMessage>? _messagesById;
```

2. 新增私有失效方法（**必须在锁内调用**）：

```csharp
private void InvalidateMessagesCache()
{
    _messagesSnapshot = null;
    _messagesById = null;
}
```

3. `Messages` getter / setter：

```csharp
/// <summary>
/// 消息列表（写时失效的锁内快照）。
/// <para><b>只读视图</b>：同一版本内多次读取返回同一实例，调用方不得 Cast 为 <see cref="List{T}"/>
/// 或就地修改 —— 任何修改都不会经过 <see cref="InvalidateMessagesCache"/>，会污染会话真相。
/// 写入请使用 <see cref="AddMessage"/> 等统一编辑 API。</para>
/// <para>返回值不是活列表：持有后不会与并发 <see cref="AddMessage"/> 竞争枚举，但也不会观察到后续变化。</para>
/// </summary>
public IReadOnlyList<SessionMessage> Messages
{
    get
    {
        lock (_messagesGate)
            return _messagesSnapshot ??= _messages.ToList();
    }
    set
    {
        lock (_messagesGate)
        {
            _messages = value is List<SessionMessage> list
                ? list
                : value?.ToList() ?? new List<SessionMessage>();
            InvalidateMessagesCache();
        }
    }
}
```

4. 新增 `TryGetMessage`：

```csharp
/// <summary>
/// 按消息 Id 取会话消息（锁内惰性建索引，任何容器写入后失效）。
/// <para><b>注意</b>：索引只跟踪「容器成员」，不感知元素 Id 的后续改写。
/// 需要按 Id 查找前，应确保消息 Id 已稳定（不要先建索引再补写 Id）。</para>
/// </summary>
public bool TryGetMessage(string? messageId, out SessionMessage message)
{
    message = null!;
    if (string.IsNullOrEmpty(messageId))
        return false;

    lock (_messagesGate)
    {
        if (_messagesById is null)
        {
            var index = new Dictionary<string, SessionMessage>(_messages.Count, StringComparer.Ordinal);
            foreach (var m in _messages)
            {
                if (!string.IsNullOrEmpty(m.Id))
                    index[m.Id] = m;
            }
            _messagesById = index;
        }

        if (!_messagesById.TryGetValue(messageId, out var found))
            return false;

        message = found;
        return true;
    }
}
```

5. **所有对 `_messages` 的写点**在锁内改完后调用 `InvalidateMessagesCache()`。实际写点（已核实）：

| 行 | 方法 |
|---|---|
| `:262` | `AddMessage` |
| `:281` | `AddMessages`（锁内） |
| `:301` | `InsertMessage` |
| `:326` | `RemoveMessage`（仅 Remove 成功时） |
| `:348` | `RemoveLastMessage`（仅命中时） |
| `:366` | `RemoveMessages`（仅 `removed > 0` 时） |
| `:386/391` | `ReplaceMessages` 类方法（锁内） |
| `:433` | `ClearMessages` |

> `:497` 是 `Clone()` 的对象初始化器，创建的是**新对象**，不需要失效。

6. `GetActiveMessages`：

```csharp
/// <summary>
/// 获取活跃消息列表：应传递给 LLM 的统一消息来源（以最后一个摘要为界）。
/// <para>返回值是独立副本，调用方可安全持有与修改。</para>
/// </summary>
public List<SessionMessage> GetActiveMessages()
{
    // 基于缓存的 Messages 快照遍历，避免为「找最后一个摘要」再复制一次全表
    var snapshot = Messages;
    var lastSummaryIndex = -1;
    for (var i = snapshot.Count - 1; i >= 0; i--)
    {
        if (snapshot[i].IsSummary) { lastSummaryIndex = i; break; }
    }

    return lastSummaryIndex < 0
        ? new List<SessionMessage>(snapshot)
        : snapshot.Skip(lastSummaryIndex).ToList();
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionMessageSnapshotTests"`
Expected: 9 个测试全 PASS。

- [ ] **Step 5: 全量回归**

Run: `dotnet build Seeing.Agent.slnx` → `dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll`
Expected: 构建通过，既有测试无新增失败。

- [ ] **Step 6: 提交**

```bash
git add src/primitives/Seeing.Session/Core/SessionData.cs tests/primitives/Seeing.Session.Tests/SessionMessageSnapshotTests.cs
git commit -m "perf(session): 消息快照缓存与按 Id 索引，消除热路径全表复制"
```

---

## Task 10: 关系模型与会话分区预计算（可选收尾）

> **低优先级**：本任务对用户诉求的两个场景基本无收益（关系条仅在 `HasRelatedSessions` 时渲染，抽屉默认关闭，M ≤ 10）。仅在 Task 1-9 完成后有余力时执行。

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionRelationModel.cs:86-120, 126-163`
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionSidebarView.cs:30-33, 68-81`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionRelationBar.razor:55-58`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionRelationDrawer.razor:151`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor:143-178`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionRelationModelCacheTests.cs`（新建）

**Interfaces:**
- Produces:
  - `IReadOnlyList<RelationNode> RelationTreeModel.BarVisible { get; init; }`
  - `int RelationTreeModel.BarOverflow { get; init; }`
  - `IReadOnlyList<RelationRow> RelationTreeModel.Rows { get; init; }`
  - `int SidebarSection.PendingPermissionTotal { get; init; }`

> 现状：`BarItems` 是**静态元组方法** `(IReadOnlyList<RelationNode> Visible, int Overflow) BarItems(IReadOnlyList<RelationNode> mainLine, int max = 3)`（`SessionRelationModel.cs:146`）；`BuildRows()` 是每次调用都重算的实例方法（`:86`）；`RelationTreeModel` 是 `sealed class`（**不是 record，不能用 `with`**）。

- [ ] **Step 1: 写失败测试**

新建测试，断言（**不要**用 `Rows.Should().BeEquivalentTo(model.BuildRows())` —— 实现后那是 `Rows == Rows` 的同义反复）：

```csharp
[Fact]
public void BarVisible_WithShortMainLine_ShouldContainAnchorAndNoOverflow()
{
    var model = SampleModel();   // 1 锚点 + 2 子会话

    model.BarVisible.Should().Contain(n => n.IsAnchor);
    model.BarOverflow.Should().Be(0);
}

[Fact]
public void BarVisible_WithLongMainLine_ShouldKeepFirstAndAnchor()
{
    // 5 个交接链节点，max=3
    var model = RelationTreeModel.Build(FiveHandoffChain());

    model.BarVisible.Should().HaveCount(2);
    model.BarOverflow.Should().Be(3);
}

[Fact]
public void Rows_ShouldPreserveBuildRowsSemantics()
{
    var model = SampleModel();

    // 显式断言内容而非同义反复
    model.Rows.Should().HaveCount(3);
    model.Rows[0].Depth.Should().Be(0);
    model.Rows.Skip(1).Should().OnlyContain(r => r.Depth == 1);
}

[Fact]
public void SidebarSection_PendingPermissionTotal_ShouldSumEntries()
    => SessionSidebarView.Build([
            new SidebarEntry("a", "A", SessionRelation.None, true, false, null, null, false, 2),
            new SidebarEntry("b", "B", SessionRelation.Child, false, false, "a", null, false, 3)])
        .Sum(s => s.PendingPermissionTotal).Should().Be(5);
```

> 构造 `RelationNode`：`new(id, id, relation, isAnchor, IsExecuting: false, parent, Label: null, IsActive: false)`（签名见 `SessionRelationModel.cs:15-24`）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Expected: 编译失败（`BarVisible`/`BarOverflow`/`Rows`/`PendingPermissionTotal` 未定义）。

- [ ] **Step 3: 预计算**

1. `RelationTreeModel` 新增三个 `init` 属性。
2. 把原 `BuildRows()` 方法体抽为 `private static IReadOnlyList<RelationRow> ComputeRows(mainLine, derived, children)`。
3. `Build(...)` 改为：先算 `derived`/`children` 局部变量 → `var (visible, overflow) = BarItems(mainLine)` → 一次构造并填入全部属性。
4. `BuildRows()` 保留为公开方法，方法体改为 `return Rows;`。
5. `SidebarSection` 加 `int PendingPermissionTotal { get; init; } = 0;`，`SessionSidebarView.AddSection` 里 `matched.Sum(e => e.PendingPermissions)` 传入。
6. `SessionRelationBar.razor`：删私有 `Bar` 属性，标记改读 `Model.BarOverflow` / `Model.BarVisible`。
7. `SessionRelationDrawer.razor:151`：`Model?.BuildRows()` → `Model?.Rows`。
8. `Session.razor:154-156`：`sec.Entries.Any(e => e.PendingPermissions > 0)` → `sec.PendingPermissionTotal > 0`；`sec.Entries.Sum(...)` → `sec.PendingPermissionTotal`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet build Seeing.Agent.slnx` → `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~SessionRelationModelCacheTests"`
Expected: 4 个测试 PASS。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SessionRelationModel.cs samples/Seeing.Agent.WebUI/Services/SessionSidebarView.cs samples/Seeing.Agent.WebUI/Components/SessionRelationBar.razor samples/Seeing.Agent.WebUI/Components/SessionRelationDrawer.razor samples/Seeing.Agent.WebUI/Pages/Session.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionRelationModelCacheTests.cs
git commit -m "perf(webui): 关系模型与会话分区预计算，去掉 render 期 O(M²) LINQ"
```

---

## 收尾

### 文档同步（必要，否则文档漂移）

- [ ] **新增 release notes**：`docs/architecture/27-webui-render-performance-release-notes.md`，按 `26-memory-page-redesign-release-notes.md` 的格式（日期 / 设计规格 / 实施计划 / 变更摘要 / 已知边界），内容至少覆盖：
  - `SessionData.Messages` 语义变更（每次新 List → 同版本同实例，只读视图）
  - 新增 `SessionData.TryGetMessage`
  - Summary 窗口不再构建时间线/订阅完整事件流（行为变更）
  - 切换会话不再整窗重建（`@key` 移除）
  - 流式期跳过 Markdown 的行为差异
  - `RenderCoalescer` 33ms 窗口；`ScrollInteropThrottle` 40ms
  - 已知边界（见下）
- [ ] **登记到** `docs/architecture/README.md` 的文档索引表
- [ ] **登记到** `AGENTS.md` 的架构文档列表
- [ ] **更新** `docs/architecture/04-development-standards.md` §5.1：原「与 `SessionData.Messages` 的"锁内快照"契约互补」一句需改写为「写时失效缓存 + 只读视图」的表述

### 回归验证

- [ ] `dotnet build Seeing.Agent.slnx`（0 error）
- [ ] `dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll`
- [ ] `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
- [ ] `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`
- [ ] 若仓库存在 `Seeing.Agent.Invariants.Tests`（`docs/architecture/04` 要求），一并运行

### 端到端验收（回填 Task 0 的结果文件）

对 Task 0 的 6 组基线数据逐项复测，形成前后对照表。目标不是绝对数值，而是**相对基线**：

- [ ] 场景①：流式期渲染通道数下降；`Recalculate Style` + `Layout` 次数下降；长任务数下降
- [ ] 场景②：切换 `session-window-first-render` P50 下降 ≥ 50%；侧栏 Summary 窗口重建次数应显著低于切换次数（Task 2 后同一窗口被复用）
- [ ] 多 agent 三子会话并发流式时，主区点击响应无可感知延迟

### 已知边界（写入 release notes）

- `RenderCoalescer` 是**每窗口**独立，多 agent 场景下 N 个 Full 窗口各自 33ms 一次；circuit 级统一调度未做。
- `TryLoadMoreAsync` 与贴底判定共享同一个限流窗口，极端快速滚动时懒加载触发可能延后一个窗口（≤40ms）。
- `SerialGate` 用 `Monitor`，**可重入**（`SemaphoreSlim` 不可重入），这是有意的语义变更。
- `SessionData.TryGetMessage` 不感知元素 Id 的后续改写；若将来仍需在 UI 层补写消息 Id，必须同时失效索引。
- Summary 窗口保留事件流订阅（执行态事件是卡片状态唯一可靠来源），但只放行 `ExecutionStarted`/`ExecutionComplete`/`LoopComplete`/`SessionTitleChanged` 四类事件；时间线管道（Markdown 重建 + 分段渲染）不执行。回放期 ≤100 条事件会在 `OnHandlerChanged` 最外层被丢弃，不进入 Dispatcher 队列。

---

## 风险与回滚

| Task | 风险 | 缓解 | 回滚 |
|---|---|---|---|
| 1 | Summary 卡片状态不更新（过滤条件漏了某类事件） | Step 8 的四类事件白名单 + Step 7 第 4 项验证 | 放宽白名单，或临时移除 `ViewMode` 过滤退回完整管道（性能收益丢失但仍正确） |
| 2 | 复用窗口残留旧状态 | Step 5 的 4 项补齐 + Step 8 的 9 项手工验证 | 恢复 `@key`（Task 1 的收益仍在） |
| 3 | 流式纯文本塌行 | Global Constraint + Step 5 的 CSS | 只回滚 `RenderMarkdownPolicy` 调用点，保留 CSS |
| 4 | 流式结束后仍纯文本 | Step 6 的 Touch 清单 + Step 7 的守门测试 | 把 `StructureRevision` 传 `ContentRevision` 强制全量重建（一行改动） |
| 4 | 分段 Id 冲突导致内容串位 | `BuildLoopSegments` 的重复 Id 消歧 + 测试 | 回退到 `BuildFromLoopMessages`（该方法本计划保留到 Step 10 才删） |
| 5 | 索引与列表失步 | Step 3 的 10 处维护点 + 行为锁测试 | 删除索引，退回全表扫描（公开 API 未变） |
| 6 | 渲染延迟影响交互手感 | 仅接入内容刷新路径，`SyncContextState` 保持即时 | 把窗口从 33ms 调到 0，或移除 `Request` 调用 |
| 6 | Timer 线程异常终止进程 | `RenderIfPending` 内 try/catch + 测试 | 删除 coalescer，退回直接 `StateHasChanged` |
| 7 | `Monitor` 可重入语义差异 | 测试锁住可重入行为 | 恢复 `SemaphoreSlim` |
| 8 | 限流导致贴底落后 | `_scrollDirty` 补偿 | 移除限流判定 |
| 9 | 快照返回共享实例被外部修改 | `IReadOnlyList` + XML 注释「禁止 Cast」 | 改回每次 `ToList()`（Task 7 的 `TryGetMessage` 需同步回退） |

**跨任务回滚注意**：
- Task 7 Step 8 依赖 Task 9 的 `TryGetMessage`。若 Task 9 回滚，Task 7 该步也要回退（步骤已标注「若 Task 9 尚未做则跳过」）。
- Task 6 的 `_renderCoalescer` 被 Task 7 使用。回滚 Task 6 时 Task 7 的 `OnAggregatorAssistantChanged` 要改回直接 `StateHasChanged()`。
- Task 2 依赖 Task 1 的 `RebuildSummarySnapshot`（`ApplySessionAsync` 里 `SyncContextState` 会调它）。回滚 Task 1 时 Task 2 不受影响（`SyncContextState` 里的调用是条件式的）。
