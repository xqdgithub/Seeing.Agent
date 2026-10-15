# 多 Agent 会议大屏页面 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 基于 WebUI 事件改造构建多 Agent 会议式大屏页面：抽取可复用 `SessionWindow` 组件，主窗口 + 小窗口同时实时渲染、点击主从交换，支持未来 agent 团队扩展。

**Architecture:** 新增 `Conference.razor`（`/conference/{sessionId}`）组装 1 个 Full 窗口 + N 个 Summary 窗口；`SessionWindow` 从 `Session.razor` 抽取（每窗口自建 `SessionState`/`MessageTimelineStore`，经 `SessionEventStreamRouter` 独立订阅）；`ConferenceRegistry` 经泛化的 Router circuit consumer 维护子会话窗口集合；`TaskSessionResolver` 与 `SessionWindowTimelineSync` 抽取为可单测纯类。改动全部位于 `samples/Seeing.Agent.WebUI` + `tests/Seeing.Agent.WebUI.Tests`。

**Tech Stack:** Blazor Server (net10.0)、AntDesign 1.6.2、xUnit 3 + Moq 4.20 + FluentAssertions（VSTest 运行，禁用 MTP）

**Spec:** `docs/superpowers/specs/2026-08-28-multi-agent-conference-design.md`（两轮架构审查签字通过）

---

## 0. 通用约定

**测试运行**（AGENTS.md：MTP 无法发现测试，一律 VSTest）：
```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"
```

**整体构建**：`dotnet build Seeing.Agent.slnx`

**提交规范**：每个任务独立提交，消息中文、格式 `feat(webui): ...` / `refactor(webui): ...` / `test(webui): ...`。

**术语**：Full = 完整模式窗口（完整消息+输入）；Summary = 摘要模式窗口（摘要卡片）。

---

## 文件结构总览

```
samples/Seeing.Agent.WebUI/
├── Services/
│   ├── TaskSessionResolver.cs        # 新增：共享 taskId 解析（Task 1）
│   ├── SessionEventStreamRouter.cs   # 修改：circuit consumer 泛化（Task 2）
│   ├── SessionWindowTimelineSync.cs  # 新增：纯类事件→时间线分派（Task 3）
│   ├── SessionWindowContext.cs       # 新增：窗口对外状态与操作（Task 6）
│   ├── ConferenceRegistry.cs         # 新增：子会话窗口注册表（Task 7）
├── Components/
│   ├── MessageList.razor             # 修改：参数化 store + IdPrefix（Task 4）
│   ├── SessionWindow.razor           # 新增：通用会话窗口（Task 6）
├── Pages/
│   ├── Session.razor                 # 重构：瘦身为头部 + SessionWindow（Task 6/8）
│   ├── Conference.razor              # 新增：大屏页（Task 8）
├── State/
│   └── SessionState.cs               # 修改：实现 IDisposable（Task 5）
├── Program.cs                        # 修改：AddScoped<TaskSessionResolver>（Task 1）

tests/Seeing.Agent.WebUI.Tests/Services/
├── TaskSessionResolverTests.cs       # 新增（Task 1）
├── SessionEventStreamRouterTests.cs  # 修改：泛化/多 key 用例（Task 2）
├── SessionWindowTimelineSyncTests.cs # 新增（Task 3）
├── SessionStateTests.cs              # 新增：IDisposable 释放路径（Task 5）
├── MessageTimelineStoreTests.cs      # 修改：多实例隔离用例（Task 5）
├── ConferenceRegistryTests.cs        # 新增（Task 7）
└── TaskCardAggregatorTests.cs        # 修改：构造器适配 + 回归（Task 1）
```

---

## Task 1: 提取共享 helper `TaskSessionResolver`

从 `TaskCardAggregator.ResolveTaskIdAsync`（TaskCardAggregator.cs:172-192）提取为 Scoped 服务，aggregator 与未来 registry 共用。

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/TaskSessionResolver.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs:35-39,172-192`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs:70`（AddScoped 注册）
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/TaskSessionResolverTests.cs`
- Modify: `tests/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs`（构造器适配）

- [ ] **Step 1: 写失败测试**

创建 `tests/Seeing.Agent.WebUI.Tests/Services/TaskSessionResolverTests.cs`：

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class TaskSessionResolverTests
{
    private static SessionData CreateChild(string childId, string parentId, string? originToolCallId)
    {
        var child = SessionData.Create("p1", "explore");
        child.Id = childId;
        child.Kind = SessionKind.SubAgent;
        child.ParentSessionId = parentId;
        if (!string.IsNullOrEmpty(originToolCallId))
            child.Metadata[SessionMetadataKeys.OriginToolCallId] = originToolCallId;
        return child;
    }

    [Fact]
    public async Task ResolveTaskIdAsync_ToolCallAlreadyHasTaskId_ShouldReturnDirectly()
    {
        var sm = new Mock<ISessionManager>();
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-1", TaskId = "task-9" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("task-9");
        sm.Verify(m => m.ListChildrenAsync(It.IsAny<string>(), It.IsAny<SessionKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveTaskIdAsync_MemoryCacheMatch_ShouldResolveByOriginToolCallId()
    {
        var child = CreateChild("child1", "parent1", "call-1");
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync("parent1", SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-1" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("child1");
        sm.Verify(m => m.LoadChildrenFromStorageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveTaskIdAsync_ColdCacheFallback_ShouldQueryDiskWhenNoMemoryMatch()
    {
        var child = CreateChild("child1", "parent1", "call-1");
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync("parent1", SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync("parent1", It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-1" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("child1");
    }

    [Fact]
    public async Task ResolveTaskIdAsync_NoMatch_ShouldReturnNull()
    {
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync("parent1", SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync("parent1", It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-9" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().BeNull();
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskSessionResolverTests"
```
Expected: 编译失败（`TaskSessionResolver` 不存在）。

- [ ] **Step 3: 实现 `TaskSessionResolver`**

创建 `samples/Seeing.Agent.WebUI/Services/TaskSessionResolver.cs`：

```csharp
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 共享的 task 工具调用 → 子会话 ID 解析器（Scoped，注入 ISessionManager）。
/// 从 TaskCardAggregator.ResolveTaskIdAsync 提取，TaskCardAggregator 与 ConferenceRegistry 共用：
/// origin_tool_call_id 精确匹配（内存缓存枚举 → 磁盘冷缓存兜底）。
/// </summary>
public sealed class TaskSessionResolver
{
    private readonly ISessionManager _sessionManager;

    public TaskSessionResolver(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    /// <summary>
    /// 解析 task 工具调用对应的子会话 ID。
    /// 优先返回 toolCall.TaskId（续跑分支场景已写入）；否则按 origin_tool_call_id 匹配，
    /// 内存缓存枚举未命中时走磁盘冷缓存兜底。
    /// </summary>
    public async Task<string?> ResolveTaskIdAsync(string parentSessionId, SessionToolCall toolCall)
    {
        if (toolCall == null)
            return null;

        if (!string.IsNullOrEmpty(toolCall.TaskId))
            return toolCall.TaskId;

        if (string.IsNullOrEmpty(parentSessionId))
            return null;

        var children = await _sessionManager.ListChildrenAsync(parentSessionId, SessionKind.SubAgent);
        var match = children?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        if (match != null)
            return match.Id;

        var diskChildren = await _sessionManager.LoadChildrenFromStorageAsync(parentSessionId);
        match = diskChildren?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        return match?.Id;
    }
}
```

- [ ] **Step 4: TaskCardAggregator 改走共享 resolver**

修改 `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs`：

构造器（第 35-39 行）注入 resolver 并保存：
```csharp
private readonly TaskSessionResolver _taskResolver;

public TaskCardAggregator(
    SessionEventStreamRouter router,
    ISessionManager sessionManager,
    TaskSessionResolver taskResolver)
{
    _router = router;
    _sessionManager = sessionManager;
    _taskResolver = taskResolver ?? throw new ArgumentNullException(nameof(taskResolver));
}
```

删除私有方法 `ResolveTaskIdAsync`（第 172-192 行），替换为委托：
```csharp
private Task<string?> ResolveTaskIdAsync(SessionToolCall toolCall)
    => _taskResolver.ResolveTaskIdAsync(ParentSessionId ?? string.Empty, toolCall);
```

修改 `samples/Seeing.Agent.WebUI/Program.cs`，在 `AddScoped<TaskCardAggregator>()`（第 70 行）附近新增：
```csharp
builder.Services.AddScoped<TaskSessionResolver>();
```

- [ ] **Step 5: 适配测试构造器（两处）**

**(a)** 修改 `tests/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs`：全部 6 处 `new TaskCardAggregator(router, sm.Object)`（第 80/127/203/260/333/400 行）改为：
```csharp
var aggregator = new TaskCardAggregator(router, sm.Object, new TaskSessionResolver(sm.Object));
```

**(b)** 修改 `tests/Seeing.Agent.WebUI.Tests/Services/SessionEventStreamRouterTests.cs` 第 309 行（I2 用例内嵌的 TaskCardAggregator 工厂）：
```csharp
sp.Setup(s => s.GetService(typeof(TaskCardAggregator)))
    .Returns(() => new TaskCardAggregator(null!, Mock.Of<ISessionManager>(), new TaskSessionResolver(Mock.Of<ISessionManager>())));
```

- [ ] **Step 6: 运行全部相关测试**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskSessionResolverTests|FullyQualifiedName~TaskCardAggregatorTests"
```
Expected: 全部 PASS。

- [ ] **Step 7: 整体构建**

```bash
dotnet build Seeing.Agent.slnx
```
Expected: 无编译错误。

- [ ] **Step 8: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/TaskSessionResolver.cs samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs samples/Seeing.Agent.WebUI/Program.cs tests/Seeing.Agent.WebUI.Tests/Services/TaskSessionResolverTests.cs tests/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs
git commit -m "refactor(webui): 提取共享 TaskSessionResolver，TaskCardAggregator 改走注入"
```

---

## Task 2: Router circuit consumer 泛化（前置改造）

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/SessionEventStreamRouterTests.cs`

- [ ] **Step 1: 写失败测试**

在 `tests/Seeing.Agent.WebUI.Tests/Services/SessionEventStreamRouterTests.cs` 末尾追加：

```csharp
private sealed class FakeRegistryConsumer : IStreamConsumer
{
    public string SessionId { get; }
    public FakeRegistryConsumer() : this("") { }
    public FakeRegistryConsumer(string sessionId) => SessionId = sessionId;
    public void OnEvent(IMessageEvent evt) { }
    public void OnStreamEnd() { }
}

// GetOrCreateCircuitConsumer 经 CreateConsumer 走 scope 工厂解析实例；
// 必须配置 scopeFactory mock（仿照既有 I2 用例），否则 CreateScope() 返回 null → NRE。
// GetService 每次返回新实例（Router 幂等复用决定实例归属，跨 circuit/key 应为不同实例）。
private static IServiceScopeFactory CreateScopeFactoryFor<T>() where T : IStreamConsumer, new()
{
    var scope = new Mock<IServiceScope>();
    var sp = new Mock<IServiceProvider>();
    scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
    sp.Setup(s => s.GetService(typeof(T))).Returns(() => new T());
    var factory = new Mock<IServiceScopeFactory>();
    factory.Setup(f => f.CreateScope()).Returns(scope.Object);
    return factory.Object;
}

[Fact]
public void GetOrCreateCircuitConsumer_ShouldReuseInstancePerCircuitAndKey()
{
    var scopeFactory = CreateScopeFactoryFor<FakeRegistryConsumer>();
    using var router = CreateRouter(new Mock<IChatOrchestrator>(), scopeFactory);
    var c1 = router.GetOrCreateCircuitConsumer<FakeRegistryConsumer>("circuit-a");
    var c2 = router.GetOrCreateCircuitConsumer<FakeRegistryConsumer>("circuit-a");
    var c3 = router.GetOrCreateCircuitConsumer<FakeRegistryConsumer>("circuit-b");
    var c4 = router.GetOrCreateCircuitConsumer<FakeRegistryConsumer>("circuit-a", "key-1");

    c1.Should().BeSameAs(c2);
    c1.Should().NotBeSameAs(c3);
    c1.Should().NotBeSameAs(c4); // 多 key 共存（S5）
}

[Fact]
public void GetOrCreateCircuitConsumer_ShouldRegisterForCircuitCleanup()
{
    var scopeFactory = CreateScopeFactoryFor<FakeRegistryConsumer>();
    using var router = CreateRouter(new Mock<IChatOrchestrator>(), scopeFactory);
    var consumer = router.GetOrCreateCircuitConsumer<FakeRegistryConsumer>("circuit-a");
    // Attach 到某会话后再由 DetachAllForCircuit 统一释放（内部 _consumerCircuit 登记）
    router.AttachConsumer("s1", consumer);
    router.DetachAllForCircuit("circuit-a");

    // 释放后 AttachConsumer 不应残留订阅（Router 状态内部清理；此处验证不再抛异常即可）
    var evt = new LoopStartEvent { SessionId = "s1", LoopId = "l1" };
    var channel = Channel.CreateUnbounded<IMessageEvent>();
    var orchestrator = new Mock<IChatOrchestrator>();
    orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
        .Returns(channel.Reader.ReadAllAsync());
    using var router2 = CreateRouter(orchestrator, scopeFactory);
    router2.AttachConsumer("s1", consumer); // 重新挂载不异常
}
```

注意：`Channel` 命名空间已在该测试文件顶部 using（`System.Threading.Channels`）。

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionEventStreamRouterTests"
```
Expected: 编译失败（`GetOrCreateCircuitConsumer` 不存在）。

- [ ] **Step 3: 泛化 Router**

修改 `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`：

**(a)** `_circuitConsumersByKey` 字段（第 64 行）键升级三元组：
```csharp
/// <summary>幂等复用键（circuit 维度，I2）：(circuit, type, key) → consumer（同 circuit 跨会话复用；key 支持多实例）</summary>
private readonly ConcurrentDictionary<(string circuit, Type type, string key), IStreamConsumer> _circuitConsumersByKey = new();
```

**(b)** `GetOrCreateConsumer<T>`（第 141-167 行）中 TaskCardAggregator 特判改为调用泛化方法：
```csharp
public T GetOrCreateConsumer<T>(string sessionId, string circuitId) where T : IStreamConsumer
{
    ObjectDisposedException.ThrowIf(_disposed, this);

    // I2：TaskCardAggregator 为 circuit 维度消费者，同 circuit 跨会话复用
    if (typeof(T) == typeof(TaskCardAggregator))
        return (T)(object)GetOrCreateCircuitConsumer<TaskCardAggregator>(circuitId);

    // ... 其余逻辑保持不变
}
```

**(c)** 新增公共泛化方法，替代私有 `GetOrCreateCircuitScopedConsumer`：
```csharp
/// <summary>
/// 获取/创建 circuit 维度的 Scoped 消费者（同 circuit 跨会话复用，I2 方案 A）。
/// 不登记为任何 (circuit, session) 的主 consumer——仅在 circuit 关闭时统一释放。
/// key 为可选实例键（默认空串 = 单实例语义）；未来"每父会话一个聚合器"以父会话 Id 为 key。
/// </summary>
public T GetOrCreateCircuitConsumer<T>(string circuitId, string? key = null) where T : IStreamConsumer
{
    ObjectDisposedException.ThrowIf(_disposed, this);

    var registryKey = (circuitId, typeof(T), key ?? string.Empty);
    if (_circuitConsumersByKey.TryGetValue(registryKey, out var existing))
        return (T)existing;

    var consumer = CreateConsumer<T>(string.Empty);
    _consumerCircuit[consumer] = circuitId;

    // 竞争窗口：另一线程刚登记同 (circuit, type, key) → 释放新建资源，复用既有实例
    if (!_circuitConsumersByKey.TryAdd(registryKey, consumer))
    {
        ReleaseConsumer(consumer);
        return (T)_circuitConsumersByKey[registryKey];
    }
    return consumer;
}
```

**(d)** 删除私有方法 `GetOrCreateCircuitScopedConsumer`（第 173-189 行）。

**(e)** `ReleaseConsumer`（第 319-329 行）清理循环适配三元组键（元素类型变化，解构 `(key, value)` 不变，语义已覆盖）：
```csharp
foreach (var (key, value) in _circuitConsumersByKey.ToArray())
{
    if (ReferenceEquals(value, consumer))
        _circuitConsumersByKey.TryRemove(key, out _);
}
```

- [ ] **Step 4: 同步现有调用点**

`Session.razor:1560` 的 `GetOrCreateConsumer<TaskCardAggregator>(sessionId, circuitId)` 改为：
```csharp
_aggregator = Router.GetOrCreateCircuitConsumer<TaskCardAggregator>(circuitId);
```

- [ ] **Step 5: 运行测试**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionEventStreamRouterTests"
```
Expected: 全部 PASS（含新增 2 用例 + 既有回归）。

- [ ] **Step 6: 整体构建**

```bash
dotnet build Seeing.Agent.slnx
```
Expected: 无编译错误。

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs samples/Seeing.Agent.WebUI/Pages/Session.razor tests/Seeing.Agent.WebUI.Tests/Services/SessionEventStreamRouterTests.cs
git commit -m "refactor(webui): Router circuit consumer 泛化 GetOrCreateCircuitConsumer 支持多实例键"
```

---

## Task 3: 纯类 `SessionWindowTimelineSync`

把 `Session.razor` 的 `OnHandlerChanged`（Session.razor:1574-1684）中**时间线/状态分派**逻辑抽为纯 C# 类（导航副作用留 razor 层，见 spec 6.1）。依赖：`MessageTimelineStore` + 窗口状态 getter（函数注入）+ 渲染回调。

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/SessionWindowTimelineSync.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/SessionWindowTimelineSyncTests.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/Seeing.Agent.WebUI.Tests/Services/SessionWindowTimelineSyncTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Events;
using Seeing.Agent.Execution;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionWindowTimelineSyncTests
{
    private sealed class Harness
    {
        public MessageTimelineStore Timeline { get; } = new();
        public string SessionId { get; } = "s1";
        public int Renders { get; set; }
        public string? AppliedTitle { get; set; }
        public SessionData Session { get; } = SessionData.Create("p1", "general");
        public string? LoopId { get; set; }
        public SessionMessage? StreamingMessage { get; set; }

        public SessionWindowTimelineSync Create()
        {
            Session.Id = SessionId;
            return new SessionWindowTimelineSync(
                Timeline, SessionId,
                () => Renders++,
                t => AppliedTitle = t,
                () => Session.Messages,
                () => StreamingMessage,
                () => LoopId);
        }
    }

    [Fact]
    public void ProcessEvent_StreamDelta_ShouldSyncAssistantAndRequestRender()
    {
        var h = new Harness();
        var sync = h.Create();
        h.Session.AddMessage(SessionMessage.UserMessage("hi"));
        // 模拟服务端 ChatEventTracker 已把流式内容写入 SessionData（UI 只绑定指针刷新）
        var assistant = SessionMessage.AssistantMessage("这是模拟服务端已写入的流式内容");
        assistant.Id = "m1";
        h.Session.AddMessage(assistant);
        h.StreamingMessage = assistant;

        // delta 含换行 → 触发节流强制刷新（阈值归零路径），避免 8 字符节流阈值干扰断言
        sync.ProcessEvent(new StreamDeltaEvent { SessionId = "s1", ContentDelta = "增量内容\n" });

        h.Timeline.Items.Should().Contain(i =>
            i.Turn != null && i.Turn.Messages.Any(m => m.Id == "m1"));
        h.Renders.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ProcessEvent_ExecutionComplete_ShouldCompleteTurnAndReconcile()
    {
        var h = new Harness();
        var sync = h.Create();
        h.Session.AddMessage(SessionMessage.UserMessage("hi"));
        var assistant = SessionMessage.AssistantMessage("done");
        assistant.Id = "m1";
        h.Session.AddMessage(assistant);
        h.StreamingMessage = assistant;

        sync.ProcessEvent(new ExecutionCompleteEvent { SessionId = "s1", ExecutionId = "e1", Status = ExecutionStatus.Completed });

        h.Timeline.Items.Should().Contain(i => i.Turn is { IsComplete: true });
    }

    [Fact]
    public void ProcessEvent_SessionTitleChanged_ShouldApplyTitleOnlyForOwnSession()
    {
        var h = new Harness();
        var sync = h.Create();

        sync.ProcessEvent(new SessionTitleChangedEvent { SessionId = "s1", Title = "新标题" });
        sync.ProcessEvent(new SessionTitleChangedEvent { SessionId = "other", Title = "别会话" });

        h.AppliedTitle.Should().Be("新标题");
        h.Session.Messages.Should().BeEmpty(); // 仅标题更新，无消息副作用
    }

    [Fact]
    public void ProcessEvent_OtherSessionEvent_ShouldBeFiltered()
    {
        var h = new Harness();
        var sync = h.Create();

        sync.ProcessEvent(new StreamDeltaEvent { SessionId = "other", ContentDelta = "污染" });

        h.Timeline.Items.Should().BeEmpty();
        h.Renders.Should().Be(0);
    }

    [Fact]
    public void ProcessEvent_CompactionCompleted_ShouldResetTimeline()
    {
        var h = new Harness();
        var sync = h.Create();
        h.Session.AddMessage(SessionMessage.UserMessage("old"));
        h.Session.AddMessage(SessionMessage.AssistantMessage("prev"));

        sync.ProcessEvent(new CompactionCompletedEvent { SessionId = "s1" });

        // 全量重建：消息仍在（来自 Session），Generation 递增
        h.Timeline.Generation.Should().BeGreaterThan(0);
    }
}
```

> 说明：事件类型（`StreamDeltaEvent`/`ExecutionCompleteEvent`/`SessionTitleChangedEvent`/`CompactionCompletedEvent`）与属性名需按代码库实际定义核对；如属性名有出入（如 `ContentDelta`），以 `src/Seeing.Agent.Abstractions/Events/` 实际为准调整测试与实现。

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionWindowTimelineSyncTests"
```
Expected: 编译失败（类不存在）。

- [ ] **Step 3: 实现纯类**

创建 `samples/Seeing.Agent.WebUI/Services/SessionWindowTimelineSync.cs`：

```csharp
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App.Events;
using Seeing.Agent.Events;
using Seeing.Agent.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话窗口的"事件 → 时间线/状态"纯 C# 分派（从 Session.razor.OnHandlerChanged 提取）。
/// 职责：仅操作 MessageTimelineStore 与窗口状态属性，不触碰 NavigationManager / IMessageService 等副作用对象，
/// 不触发渲染（渲染副作用经 requestRender/applyTitle 回调交由窗口组件执行）。
/// 导航类事件（NavigateEvent、CommandResultEvent.NavigationTarget）由窗口 razor 层单独处理，不进入本类。
/// </summary>
public sealed class SessionWindowTimelineSync
{
    private readonly MessageTimelineStore _timeline;
    private readonly string _sessionId;
    private readonly Action _requestRender;
    private readonly Action<string> _applyTitle;
    private readonly Func<IReadOnlyList<SessionMessage>?> _getMessages;
    private readonly Func<SessionMessage?> _getStreamingMessage;
    private readonly Func<string?> _getLoopId;

    private int _streamCharCount;
    private int _currentCodeBlockDepth;

    /// <summary>标题状态属性（S6：纯类只更新状态，渲染副作用走 applyTitle）</summary>
    public string? Title { get; set; }

    /// <summary>压缩进行中标志（由 Compaction* 事件维护，窗口渲染用）</summary>
    public bool CompactionInProgress { get; set; }

    public string? CompactionProgress { get; set; }

    public string? CompactionReasoning { get; set; }

    public SessionWindowTimelineSync(
        MessageTimelineStore timeline,
        string sessionId,
        Action requestRender,
        Action<string> applyTitle,
        Func<IReadOnlyList<SessionMessage>?> getMessages,
        Func<SessionMessage?> getStreamingMessage,
        Func<string?> getLoopId)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
        _applyTitle = applyTitle ?? throw new ArgumentNullException(nameof(applyTitle));
        _getMessages = getMessages ?? throw new ArgumentNullException(nameof(getMessages));
        _getStreamingMessage = getStreamingMessage ?? throw new ArgumentNullException(nameof(getStreamingMessage));
        _getLoopId = getLoopId ?? throw new ArgumentNullException(nameof(getLoopId));
    }

    // ---- 时间线基础操作（窗口加载/切换/清空时调用）----

    public void ResetFromSession() => _timeline.ResetFromSession(_getMessages() ?? Array.Empty<SessionMessage>(), _sessionId);

    public void ReconcileAppendFromSession() => _timeline.ReconcileAppendFromSession(_getMessages() ?? Array.Empty<SessionMessage>(), _sessionId);

    public void CompleteTurn() => _timeline.CompleteTurn(_getLoopId());

    public void SyncAssistant(bool isComplete)
    {
        var msg = _getStreamingMessage();
        if (msg == null)
        {
            ReconcileAppendFromSession();
            return;
        }
        _timeline.SyncAssistantMessage(msg, _sessionId, isComplete);
    }

    // ---- 事件分派（窗口 razor 层在 OnHandlerChanged 桥接中调用）----

    public void ProcessEvent(IMessageEvent evt)
    {
        if (evt == null)
            return;

        // 会话归属过滤：只处理当前绑定会话的事件
        if (!string.IsNullOrEmpty(evt.SessionId)
            && !string.Equals(evt.SessionId, _sessionId, StringComparison.Ordinal))
            return;

        switch (evt)
        {
            case ExecutionCompleteEvent completeEvt:
                SyncAssistant(isComplete: true);
                CompleteTurn();
                ReconcileAppendFromSession();
                break;
            case LoopCompleteEvent:
                SyncAssistant(isComplete: true);
                CompleteTurn();
                break;
            case LoopCancelledEvent:
            case ErrorEvent:
                SyncAssistant(isComplete: true);
                CompleteTurn();
                ReconcileAppendFromSession();
                break;
            case StreamDeltaEvent deltaEvt:
                HandleStreamDelta(deltaEvt);
                break;
            case StreamStartEvent:
            case ToolCallEvent:
            case StreamCompleteEvent:
            case LoopStartEvent:
            case PermissionRequestEvent:
            case PermissionResponseEvent:
            case TodoUpdateEvent:
            case ModeUpdateEvent:
                SyncAssistant(isComplete: false);
                break;
            case SessionTitleChangedEvent titleEvt:
                if (string.Equals(_sessionId, titleEvt.SessionId, StringComparison.Ordinal))
                {
                    Title = titleEvt.Title;
                    _applyTitle(titleEvt.Title);
                }
                break;
            case SessionUpdatedEvent:
                ResetFromSession();
                break;
            case CommandResultEvent commandEvt:
                if (commandEvt.NeedsRefresh)
                {
                    ResetFromSession();
                    ReconcileAppendFromSession();
                }
                // NavigationTarget 导航由窗口 razor 层处理
                break;
            case CompactionStartedEvent:
                CompactionInProgress = true;
                CompactionProgress = string.Empty;
                CompactionReasoning = string.Empty;
                break;
            case CompactionDeltaEvent cde:
                if (!string.IsNullOrEmpty(cde.ContentDelta))
                    CompactionProgress += cde.ContentDelta;
                if (!string.IsNullOrEmpty(cde.ReasoningDelta))
                    CompactionReasoning += cde.ReasoningDelta;
                break;
            case CompactionCompletedEvent:
                CompactionInProgress = false;
                CompactionProgress = null;
                CompactionReasoning = null;
                ResetFromSession();
                ReconcileAppendFromSession();
                break;
            case CompactionFailedEvent cfe:
                CompactionInProgress = false;
                CompactionProgress = null;
                CompactionReasoning = null;
                ResetFromSession();
                ReconcileAppendFromSession();
                break;
            default:
                SyncAssistant(isComplete: false);
                ReconcileAppendFromSession();
                break;
        }

        _requestRender();
    }

    private void HandleStreamDelta(StreamDeltaEvent deltaEvt)
    {
        var contentLen = deltaEvt.ContentDelta?.Length ?? 0;
        var reasoningLen = deltaEvt.ReasoningDelta?.Length ?? 0;
        _streamCharCount += contentLen + reasoningLen;

        var inCodeBlock = _currentCodeBlockDepth > 0;
        var hasNewline = deltaEvt.ContentDelta?.Contains("\n") == true;
        var threshold = inCodeBlock ? 20 : 8;

        if (deltaEvt.ContentDelta?.Contains("```") == true)
        {
            _currentCodeBlockDepth += deltaEvt.ContentDelta.Count(c => c == '`') / 3;
            _currentCodeBlockDepth = Math.Max(0, _currentCodeBlockDepth % 2 == 0 ? 0 : 1);
            threshold = 0;
        }

        if (_streamCharCount >= threshold || hasNewline || threshold == 0)
        {
            SyncAssistant(isComplete: false);
            _streamCharCount = 0;
        }
    }
}
```

> 迁移来源（Session.razor）：`OnHandlerChanged`（:1574-1684）的时间线分支、`HandleStreamDeltaThrottled`（:1690-1713）、`ResetTimelineFromSession`（:744-756）、`ReconcileTimelineFromSession`（:761-768）、`CompleteCurrentTimelineTurn`（:770-775）、`SyncTimelineAssistantFromStream`（:811-822）。导航分支（`NavigateEvent`、`CommandResultEvent.NavigationTarget`）与 `CompactionFailedEvent` 的 `MessageService.Error` 提示留在窗口 razor 层。

- [ ] **Step 4: 运行测试**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionWindowTimelineSyncTests"
```
Expected: 全部 PASS。若事件类型/属性名与实际不符，按编译错误修正测试与实现（以 `src/Seeing.Agent.Abstractions/Events/` 定义为准）。

- [ ] **Step 5: 整体构建**

```bash
dotnet build Seeing.Agent.slnx
```

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SessionWindowTimelineSync.cs tests/Seeing.Agent.WebUI.Tests/Services/SessionWindowTimelineSyncTests.cs
git commit -m "feat(webui): 新增 SessionWindowTimelineSync 纯类事件时间线分派"
```

---

## Task 4: `MessageList` 参数化 store 与 IdPrefix

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/MessageList.razor`

- [ ] **Step 1: 参数化 Timeline store**

`MessageList.razor` 第 8 行 `@inject MessageTimelineStore Timeline` 保留为 `_injectedTimeline`，新增参数：

```csharp
@inject MessageTimelineStore InjectedTimeline
```
（razor 中第 8 行改为 `@inject MessageTimelineStore InjectedTimeline`）

@code 新增：
```csharp
/// <summary>可选：外部传入的时间线 store（参数优先，回退 DI 注入）</summary>
[Parameter]
public MessageTimelineStore? Timeline { get; set; }

/// <summary>可选：DOM Id 前缀（多实例留位，默认空串保持现状）</summary>
[Parameter]
public string? IdPrefix { get; set; }

private MessageTimelineStore Store => Timeline ?? InjectedTimeline;

private string ContainerId => $"{IdPrefix}message-list-container";
private string ContentId => $"{IdPrefix}message-list-content";
private string ScrollAnchorId => $"{IdPrefix}message-list-scroll-anchor";
```

- [ ] **Step 2: 替换全部 `Timeline` 引用为 `Store`**

在 `MessageList.razor` 中：
- 模板内 `Items` 属性：`private IReadOnlyList<TimelineModel.TimelineItem> Items => Store.Items;`（原第 72 行）
- `OnInitialized`（第 92-98 行）：`Store.Changed += OnTimelineChanged; Store.PinRequested += OnPinRequested;`，`SyncWindowFromStore` 调用不变
- `SyncWindowFromStore`（第 124 行 `Timeline.Generation` → `Store.Generation`）
- `OnAfterRenderAsync`（第 216-217 行 `Timeline.TailKey`/`Timeline.TailRevision` → `Store.TailKey`/`Store.TailRevision`）
- `DisposeAsync`（第 300-301 行 `Timeline.Changed -=`/`Timeline.PinRequested -=` → `Store.Changed -=`/`Store.PinRequested -=`）
- 移除 const `ContainerId/ContentId/ScrollAnchorId`（第 54-56 行），改用上述属性

- [ ] **Step 3: 构建验证（行为不变）**

```bash
dotnet build Seeing.Agent.slnx
```
Expected: 无编译错误（`@inject MessageTimelineStore` 移除后无残留引用）。

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/MessageList.razor
git commit -m "refactor(webui): MessageList 参数化 Timeline store 与 IdPrefix"
```

---

## Task 5: `SessionState` 实现 IDisposable（+ 实例化隔离测试）

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/State/SessionState.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/SessionStateTests.cs`（新增）
- Modify: `tests/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTests.cs`（追加多实例隔离用例）

- [ ] **Step 1: 写失败测试**

创建 `tests/Seeing.Agent.WebUI.Tests/Services/SessionStateTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionStateTests
{
    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var state = new SessionState();
        state.Dispose();
        state.Dispose(); // 防重入：不抛异常
    }

    [Fact]
    public void Dispose_AfterStartExecution_ShouldReleaseResourcesWithoutThrow()
    {
        var state = new SessionState();
        state.StartExecution(); // 占用执行锁并创建取消令牌
        state.Dispose();        // 释放锁 + cts，不抛
    }
}
```

在既有 `tests/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTests.cs` 末尾追加多实例隔离用例（验证每窗口实例化改造）：

```csharp
[Fact]
public void TwoInstances_ShouldBeIsolated()
{
    var storeA = new MessageTimelineStore();
    var storeB = new MessageTimelineStore();
    var sessionA = SessionData.Create("p1", "general");
    sessionA.Id = "sA";
    sessionA.AddMessage(SessionMessage.UserMessage("A"));
    var sessionB = SessionData.Create("p1", "general");
    sessionB.Id = "sB";
    sessionB.AddMessage(SessionMessage.UserMessage("B"));

    storeA.ResetFromSession(sessionA.Messages, "sA");
    storeB.ResetFromSession(sessionB.Messages, "sB");

    storeA.Items.Should().Contain(i => i.Message?.Content == "A");
    storeA.Items.Should().NotContain(i => i.Message?.Content == "B");
    storeB.Items.Should().Contain(i => i.Message?.Content == "B");
    storeB.Items.Should().NotContain(i => i.Message?.Content == "A");
}
```
> 需确认该测试文件顶部已 using `Seeing.Session.Core` 与 `Seeing.Agent.WebUI.Services`（`MessageTimelineStore`），缺失则补。

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionStateTests|FullyQualifiedName~MessageTimelineStoreTests"
```
Expected: SessionStateTests 编译失败（类无 `Dispose`）。

- [ ] **Step 3: 实现 IDisposable（含防重入 + 释放 cts，建议项 7）**

`SessionState.cs`：类声明（第 14 行）改为 `public class SessionState : IDisposable`，文件末尾新增：

```csharp
private bool _disposed;

/// <summary>
/// 释放执行锁与取消令牌源（窗口自建实例时由 SessionWindow.Dispose 调用；Scoped 实例随 scope 回收）。
/// 防重入：仅首次释放，避免 Dispose 后并发 CancelExecution 操作已释放的锁。
/// </summary>
public void Dispose()
{
    if (_disposed)
        return;
    _disposed = true;
    _executionLock.Dispose();
    _cancellationTokenSource?.Dispose();
    _cancellationTokenSource = null;
}
```

同时修改 `CancelExecution()`（现有第 241 行附近）开头加防重入守卫（计划复审建议 2——Dispose 后并发取消不得操作已释放的锁；窗口 Dispose 顺序先 Cancel 后 Dispose 已安全，此处防并发残留）：

```csharp
public void CancelExecution()
{
    if (_disposed)
        return;
    // ... 其余保持原逻辑
}
```

- [ ] **Step 4: 运行测试**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionStateTests|FullyQualifiedName~MessageTimelineStoreTests"
```
Expected: 全部 PASS。

- [ ] **Step 5: 整体构建**

```bash
dotnet build Seeing.Agent.slnx
```

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/State/SessionState.cs tests/Seeing.Agent.WebUI.Tests/Services/SessionStateTests.cs tests/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTests.cs
git commit -m "feat(webui): SessionState 实现 IDisposable 释放执行锁与令牌，补实例化隔离测试"
```

---

## Task 6: 抽取 `SessionWindow` + `SessionWindowContext`，重构 `Session.razor`

这是最大任务。`SessionWindow` 承载会话加载/事件绑定/时间线/提交/取消/只读/Todo/压缩/budget/标题 + `ViewMode` 双模式；`Session.razor` 瘦身为头部 UI + 对话框。

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs`
- Create: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`

### Step 0: `SessionWindowContext`（先建）

创建 `samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs`：

```csharp
using Seeing.Agent.Execution;
using Seeing.Agent.WebUI.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// SessionWindow 对外暴露的状态与操作（供外层页头部经 Header 模板读写）。
/// 状态为窗口内部自建 SessionState 的快照；操作为窗口内实现委托。
/// </summary>
public sealed class SessionWindowContext
{
    // ---- 只读状态（窗口刷新） ----
    public string SessionId { get; internal set; } = string.Empty;
    public string Title { get; internal set; } = string.Empty;
    public SessionData? CurrentSession { get; internal set; }
    public bool IsSubAgentView { get; internal set; }
    public bool IsQueued { get; internal set; }
    public int QueuePosition { get; internal set; }
    public bool HasActiveExecution { get; internal set; }
    public bool IsExecuting { get; internal set; }
    public string SelectedAgent { get; internal set; } = string.Empty;
    public string SelectedModel { get; internal set; } = string.Empty;
    public string SelectedAcpMode { get; internal set; } = string.Empty;
    public ExecutionStatus? ExecutionStatus { get; internal set; }
    public TodoListViewModel? CurrentTodoList { get; internal set; }
    /// <summary>当前 Agent 是否 ACP 透传（窗口计算刷新，外层头部据此切换 ACP Mode/Model 输入框）</summary>
    public bool IsAcpPassthrough { get; internal set; }

    // ---- 操作（窗口内实现） ----
    public Func<string, Task>? SetAgentAsync { get; internal set; }
    public Func<string, Task>? SetModelAsync { get; internal set; }
    public Action<string>? SetAcpMode { get; internal set; }
    public Func<string, Task>? RenameAsync { get; internal set; }
    public Func<Task>? BranchAsync { get; internal set; }
    public Func<Task>? ClearAsync { get; internal set; }
    public Action? ReturnToParent { get; internal set; }
    public Func<string>? GetWorkspace { get; internal set; }

    // ---- 事件 ----
    public event Action? StateChanged;
    public event Action? SessionLoaded;

    internal void NotifyStateChanged() => StateChanged?.Invoke();
    internal void NotifySessionLoaded() => SessionLoaded?.Invoke();
}
```

### Step 1: 从 Session.razor 迁移方法（逐项）

按以下映射从 `Session.razor` 现有代码迁移到新组件 `SessionWindow.razor`。**代码从现有文件复制，只改签名/引用**：

| 源（Session.razor） | 目标（SessionWindow.razor） | 变更点 |
|---|---|---|
| `OnInitializedAsync`（:603-650） | `OnInitializedAsync` | 去掉 `LoadOptionsAsync`/`LoadToolsAndSkillsAsync`（归外层）；**`LoadCommandsFromBackend` 在窗口内实现**（PromptInput 的 `AvailableCommands` 需要）；保留会话加载 + `EnsureStreamBinding` + `RestoreExecutionFromOverview` + budget/标题订阅 |
| `LoadSessionByIdAsync`（:1248-1303） | `LoadSessionByIdAsync` | `SessionState.SetSession` 改为自建实例；**桥接仅 Full 模式写 `_globalSessionState`（见关键片段）**；`AppState.NotifyChange()` 改为 `SyncContextState()`；**不再写 `AppState.CurrentSessionId`**；`_compaction*` 字段改 `_sync.Compaction*` |
| `EnsureDefaultAgentSelectionAsync`（:1309-1345） | 迁移 | 见"选择器适配"说明（必改项 7） |
| `ReconcileIncompleteTasksAsync`（:1875-1892） | 原样迁移 | — |
| `UpdateBudgetStatus`（:827-855） | 原样迁移 | — |
| `SubscribeToBudgetUpdates`（:668-681） | 原样迁移 | `SessionState` 换自建实例 |
| `SubscribeToSessionEvents` + `OnSessionLifecycleEvent` + `ApplyTitleToUi`（:686-727） | 原样迁移 | `ApplyTitleToUi` 用 `_sync.Title` 状态 + 渲染回调 |
| `EnsureStreamBinding`（:1522-1567） | `EnsureStreamBinding` | handler 恒绑定 + `EnsureAggregatorBinding()`（独立方法，见关键片段）；`OnHandlerChanged` 改经 `_sync` |
| `OnHandlerChanged`（:1574-1684） | `OnHandlerChanged` | 先 `_sync.ProcessEvent(evt)`；再处理导航副作用 + **执行态清理（见关键片段，必改项 6）**；`_renderedTitle` 去重移入 `_sync._applyTitle` 回调 |
| `HandleStreamDeltaThrottled`（:1690-1713） | 删除 | 移入 `SessionWindowTimelineSync.HandleStreamDelta` |
| `OnAggregatorAssistantChanged`（:1718-1722） | 迁移 | **加会话过滤：仅 `SessionId == AggregatorSessionId` 时才同步（避免 root aggregator 把 root 消息注入子窗口 timeline，建议项 2）** |
| `ResetTimelineFromSession`/`ReconcileTimelineFromSession`/`CompleteCurrentTimelineTurn`/`SyncTimelineAssistantFromStream`（:744-822） | 删除 | 由 `_sync` 提供等价方法 |
| `OnSubmitMessage`（:1394-1514） | 原样迁移 | `SessionState` 换自建实例；`Router.AttachConsumer` 不变 |
| `OnCancelExecution`（:1817-1873） | 原样迁移 | `SessionState` 换自建实例 |
| `OnAgentChanged`/`OnModelChanged`/`OnAcpModeChanged`（:1014-1087） | 迁移为 `SessionWindowContext` 操作实现 | 见"选择器适配"说明（必改项 7）：去除页面字段依赖，ACP 判断内联 `AgentRegistry` 查询 |
| `OnBranchSession`（:2008-2022） | `BranchAsync` | 含导航（注入 NavigationManager） |
| `OnClearSession`（:2039-2058） | `ClearAsync` | — |
| `ReturnToParentSession`（:2027-2034） | `ReturnToParent` | 含导航 |
| `OnRenameConfirm` 的 `ChatOrchestrator.RenameSessionAsync` 部分（:1950-1959） | `RenameAsync(title)` | 不含对话框 |
| `Dispose`（:2089-2119） | `Dispose` | 追加 `_sessionState.Dispose()`；`_sync` 无额外释放；**离开 Full 退订 budget/aggregator（建议项 1）** |
| **窗口必需补充方法（全部迁入 SessionWindow）** | — | `CreateNewSessionAsync`（Full 空状态按钮）、`GetPlaceholderText`/`GetHelpText`（PromptInput）、`CurrentAutoApprove`/`OnAutoApproveChanged`、`OnCompactRequested`、`OnToolClick`（Task 卡片打开子会话导航）、`OnClearError`、`ResolveCurrentStreamingAssistantMessage`（`_sync` 构造用）、`RestoreExecutionFromOverview`、`LoadCommandsFromBackend`（**窗口内实现**——PromptInput 的 `AvailableCommands` 需要；外层不加载命令）、`GetStatusBadge`/`GetLatestAssistantPreview`（Summary 摘要渲染）、`GetWorkspace` |

**补充方法体要点（计划复审建议 4，原 Session.razor 无独立方法或头部内联，实施时按此新写）**：
- `GetWorkspace()`：`SessionState.CurrentSession?.WorkingDirectory ?? WorkspaceProvider.WorkspaceRoot`（原头部内联表达式）。
- `GetStatusBadge()`：映射 `SessionState` 执行态 → AntDesign `BadgeStatus`：`IsExecuting`/`ExecutionStatus==Running/Pending/Queued` → `Processing`/`Default`；空闲 → `Success`（默认展示）。
- `GetLatestAssistantPreview()`：取 `SessionState.Messages` 最后一条 `Role=="assistant"` 消息的 `Content`，截断约 120 字符；为空则取运行中工具调用名（`ToolCalls` 中 `Status=="running"` 的 `Name`），作为摘要卡片最新动态。Summary 模式下渲染。
| 选择器相关 `_selectedAgent/_selectedModel/...`、`SyncSelectorValues`、`LoadOptionsAsync`、对话框逻辑 | **留在 Session.razor** | 值经 `Context` 读写（同步机制见必改项 10） |

### 选择器适配（必改项 7）

`EnsureDefaultAgentSelectionAsync`/`OnAgentChanged`/`SyncSelectorValues` 原实现引用页面字段（`_selectedAgent/_models/_isAcpPassthrough/RefreshAgentRuntime`），迁入窗口后需去耦：

- **窗口侧**：会话选中的 Agent/Model 值以 `SessionState.CurrentSession.SelectedAgent/SelectedModel/SelectedAcpMode` 为唯一真相，写入 `Context.SelectedAgent/SelectedModel/SelectedAcpMode`（经 `SyncContextState()`）。
- `EnsureDefaultAgentSelectionAsync`：仅保留"空值时按配置解析并写回会话"逻辑（`AgentSelectionResolver`/`ModelManager.SeedSessionModel`），删除 `_selectedAgent`/`RefreshAgentRuntime` 写入。
- **ACP 运行时判断**：内联 `AgentRegistry.GetAgentAsync(agentId)` 查询 `Runtime==AcpPassthrough`，窗口内提供 `private Task<bool> IsAcpAgentAsync(string agentId)`，供 `OnAgentChanged`/模型校验使用（不再依赖页面 `_isAcpPassthrough` 字段）。
- `OnAgentChanged`：写 `SessionState.CurrentSession.SelectedAgent` + `SaveAsync` + `SyncContextState()`；ACP 切换时清空 SelectedModel/SelectedAcpMode（保留原语义）。
- 外层 Session.razor 的 `_isAcpPassthrough`（显示 ACP Mode/Model 输入框）改为：订阅 `Context.StateChanged` 后重算（`await Context.CurrentSession` 的 Agent → `AgentRegistry` 查询），或把判断收敛为 Context 提供的 `IsAcpPassthrough` 只读属性（窗口计算，`SyncContextState` 刷新）。**推荐后者**：`SessionWindowContext.IsAcpPassthrough` 由窗口在 agent 变化时刷新，外层头部 Select 直接消费。

### Step 2: 创建 `SessionWindow.razor`

```razor
@using AntDesign
@using Microsoft.AspNetCore.Components.Web
@using Seeing.Agent.WebUI.Models
@using Seeing.Agent.WebUI.State
@using Services = Seeing.Agent.WebUI.Services
@using Core = Seeing.Agent.Core
@using Seeing.Session.Core
@using Seeing.Agent.App
@using Seeing.Agent.App.Events
@using Seeing.Agent.Events
@using Seeing.Agent.Execution
@using Seeing.Agent.Configuration
@using Seeing.Agent.Llm
@using Seeing.Agent.Tools
@using Seeing.Agent.Skills
@using Microsoft.Extensions.Options
@inject AppState AppState
@inject Services.SessionEventStreamRouter Router
@inject Services.CircuitContext CircuitContext
@inject NavigationManager NavigationManager
@inject IMessageService MessageService
@inject IChatOrchestrator ChatOrchestrator
@inject Abstractions.Agents.IAgentRegistry AgentRegistry
@inject Core.AgentSelectionResolver AgentSelectionResolver
@inject IWorkspaceProvider WorkspaceProvider
@inject Seeing.Agent.Abstractions.Permissions.IPermissionChannel PermissionChannel
@inject ToolManager ToolInvoker
@inject SkillManager SkillManager
@inject ILlmService LlmService
@inject IModelManager ModelManager
@inject Services.CommandListService CommandListService
@inject Seeing.Agent.TokenBudget.IBudgetStatusNotifier BudgetNotifier
@inject Seeing.Agent.TokenBudget.ITokenBudgetManager TokenBudgetManager
@inject ISessionManager SessionManager
@inject ISessionEventPublisher SessionEventPublisher
@inject Microsoft.Extensions.Options.IOptionsMonitor<SeeingAgentOptions> SeeingAgentOptions
@implements IDisposable

<div Class="@($"session-window session-window--{ModeCss}")">
    @if (ViewMode == SessionWindowMode.Full)
    {
        @if (Header != null)
        {
            @Header(Context)
        }
        <div Class="session-window-content">
            @if (ShowEmptyState)
            {
                <Empty Class="session-empty-state">
                    <DescriptionTemplate>
                        <div Class="empty-content">
                            <Icon Type="@IconType.Outline.FileAdd" Class="empty-icon" Style="font-size: 48px;" />
                            <p Style="font-size: 18px; margin-top: 16px;">暂无会话</p>
                            <Button Type="@ButtonType.Primary"
                                    Icon="@IconType.Outline.Plus"
                                    OnClick="@CreateNewSessionAsync">新建会话</Button>
                        </div>
                    </DescriptionTemplate>
                </Empty>
            }
            else
            {
                <MessageList AutoScroll="true"
                             Timeline="@Timeline"
                             OnToolClick="@OnToolClick"
                             CompactionInProgress="@_sync.CompactionInProgress"
                             CompactionReasoning="@_sync.CompactionReasoning"
                             CompactionProgress="@_sync.CompactionProgress" />
            }
        </div>

        @if (!ShowEmptyState)
        {
            <div Class="session-input-container">
                @if (IsSubAgentView)
                {
                    @* 只读横幅：迁移自 Session.razor:343-361 *@
                }
                else
                {
                    <SlotHost SlotName="session_prompt" />
                    <PromptInput IsProcessing="@SessionState.HasActiveExecution"
                                 Placeholder="@GetPlaceholderText()"
                                 HelpText="@GetHelpText()"
                                 AvailableCommands="@_availableCommands"
                                 SessionState="SessionState"
                                 CancellationTokenSource="@SessionState.CancellationTokenSource"
                                 BudgetStatus="SessionState.BudgetStatus"
                                 AutoApprove="@CurrentAutoApprove"
                                 OnAutoApproveChanged="@OnAutoApproveChanged"
                                 GlobalAutoApprove="@_globalAutoApprove"
                                 AutoApproveDisabled="@IsSubAgentView"
                                 OnCompactRequested="OnCompactRequested"
                                 OnSubmitCallback="@OnSubmitMessage"
                                 OnCancelCallback="@OnCancelExecution" />
                }
            </div>
        }

        @if (SessionState.CurrentTodoList != null && SessionState.CurrentTodoList.HasTasks)
        {
            <div Class="session-todo-panel">
                <TodoPanel TodoList="@SessionState.CurrentTodoList" />
            </div>
        }

        @if (!string.IsNullOrEmpty(ErrorMessage))
        {
            <Alert Type="@AlertType.Error" Message="@ErrorMessage" ShowIcon="true" Closable="true"
                   OnClose="@OnClearError" Class="session-error-alert" />
        }
    }
    else
    {
        @* Summary 模式：摘要卡片（标题/badge/执行状态/最新消息预览/运行中工具） *@
        <div class="conference-tile" @onclick="OnTileClick">
            <div class="conference-tile-header">
                <span class="conference-tile-title">@SessionState.Title</span>
                <Badge Status="@GetStatusBadge()" />
            </div>
            <div class="conference-tile-body">
                @GetLatestAssistantPreview()
            </div>
        </div>
    }
</div>

@code {
    // ---- 参数 ----
    [Parameter] public string SessionId { get; set; } = string.Empty;
    [Parameter] public SessionWindowMode ViewMode { get; set; } = SessionWindowMode.Full;
    [Parameter] public RenderFragment<Services.SessionWindowContext>? Header { get; set; }
    [Parameter] public EventCallback<string> OnClick { get; set; }
    [Parameter] public string? AggregatorSessionId { get; set; }
    [Parameter] public Services.SessionState? GlobalSessionState { get; set; }

    // ---- 内部状态（每窗口自建） ----
    public Services.SessionWindowContext Context { get; private set; } = new();
    public SessionState SessionState { get; private set; } = new();
    public Services.MessageTimelineStore Timeline { get; private set; } = new();
    private Services.SessionState _globalSessionState;   // 桥接写入目标（注入，见下）
    private Services.SessionWindowTimelineSync _sync;
    ...
}
```

> **桥接注入**：SessionWindow 无法从 DI 注入 `_globalSessionState`（同注入冲突）。实现方式：在 `OnInitialized` 经 `IServiceProvider`（或由外层页经 `[CascadingParameter]`/参数传入）获取全局 Scoped `SessionState` 实例。**推荐方案**：`SessionWindow` 添加 `[Parameter] public Services.SessionState? GlobalSessionState`，由 `Session.razor`/`Conference.razor` 传入 DI 注入的全局实例（页面注入后下传）。Session 页与大屏页均注入全局 `SessionState` 并传给窗口。

`SessionWindow.razor` @code 完整结构（方法体迁移自 Session.razor，见 Step 1 映射表）：

```csharp
// ---- 参数 ----
// （见上）

// ---- 内部状态 ----
private Services.SessionState? _globalSessionState;
private Services.SessionWindowTimelineSync _sync;
private Services.EventStreamHandler? _handler;
private Services.TaskCardAggregator? _aggregator;
private string? _boundSessionId;
private Action<IMessageEvent>? _onHandlerChangedCallback;
private bool _hasRendered;
private string? _loadedSessionId;
private string? _renderedTitle;
private SessionWindowMode _lastViewMode = SessionWindowMode.Full;
private IDisposable? _budgetSubscription;
private IDisposable? _sessionEventSubscription;
private IDisposable? _seeingAgentOptionsSubscription;
private bool _globalAutoApprove;
private List<CommandItemViewModel> _availableCommands = new();
private string _currentStreamingMessageId = string.Empty;
private string? ErrorMessage;
private bool ShowEmptyState;
private bool IsLoadingSession;

private string ModeCss => ViewMode == SessionWindowMode.Full ? "full" : "summary";
private bool IsSubAgentView => SessionState.CurrentSession?.Kind == SessionKind.SubAgent;

protected override async Task OnInitializedAsync()
{
    _globalSessionState = GlobalSessionState;
    _sync = new Services.SessionWindowTimelineSync(
        Timeline, SessionId,
        () => _ = InvokeAsync(StateHasChanged),
        t => ApplyTitleToUi(t),
        () => SessionState.CurrentSession?.Messages,
        () => ResolveCurrentStreamingAssistantMessage(),
        () => _handler?.GetCurrentLoopId());

    _seeingAgentOptionsSubscription = SeeingAgentOptions.OnChange((opts, name) =>
    {
        _globalAutoApprove = opts.Permission?.AutoApproveAll ?? false;
        _ = InvokeAsync(StateHasChanged);
    });

    await LoadCommandsFromBackend();
    if (!string.IsNullOrEmpty(SessionId))
    {
        await LoadSessionByIdAsync(SessionId);
        _loadedSessionId = SessionId;
        EnsureStreamBinding(SessionId);
        RestoreExecutionFromOverview(SessionId);
    }
    SubscribeToBudgetUpdates();
    SubscribeToSessionEvents();

    Context.SessionId = SessionId;
    Context.SetAgentAsync = OnAgentChanged;
    Context.SetModelAsync = OnModelChanged;
    Context.SetAcpMode = OnAcpModeChanged;
    Context.RenameAsync = RenameSessionAsync;
    Context.BranchAsync = OnBranchSession;
    Context.ClearAsync = OnClearSession;
    Context.ReturnToParent = ReturnToParentSession;
    Context.GetWorkspace = GetWorkspace;
}

protected override async Task OnParametersSetAsync()
{
    // ViewMode 变化（大屏主从交换）：进入 Full 激活 aggregator/budget；离开 Full 退订 budget（handler/timeline 保留）
    if (_lastViewMode != ViewMode)
    {
        _lastViewMode = ViewMode;
        if (ViewMode == SessionWindowMode.Full)
        {
            EnsureAggregatorBinding();
            SubscribeToBudgetUpdates();
            // 主从交换路径（不重载会话）：进入 Full 时补写全局桥接（当前活跃窗口语义，必改项 10）
            if (SessionState.CurrentSession != null)
                _globalSessionState?.SetSession(SessionState.CurrentSession);
        }
        else
        {
            // 离开 Full：退订 budget 订阅（建议项 1；aggregator 保留 root 绑定，Summary 不渲染聚合结果）
            _budgetSubscription?.Dispose();
            _budgetSubscription = null;
        }
        await InvokeAsync(StateHasChanged);
    }

    if (!string.IsNullOrEmpty(SessionId) && SessionId != _loadedSessionId)
    {
        await LoadSessionByIdAsync(SessionId);
        _loadedSessionId = SessionId;
        EnsureStreamBinding(SessionId);
        RestoreExecutionFromOverview(SessionId);
    }
}
// ... 其余方法迁移自 Session.razor（见映射表），关键签名/行为：
```

**关键实现片段（迁移要点）**：

```csharp
// 加载会话（迁移自 LoadSessionByIdAsync，改造点见注释）
private async Task LoadSessionByIdAsync(string sessionId)
{
    var sessionData = await ChatOrchestrator.GetSessionAsync(sessionId);
    if (sessionData == null)
    {
        ErrorMessage = $"找不到会话: {sessionId}";
        ShowEmptyState = true;
        return;
    }

    // 压缩进度状态已移入 _sync（纯类字段）
    _sync.CompactionInProgress = false; _sync.CompactionProgress = null; _sync.CompactionReasoning = null;
    _currentStreamingMessageId = string.Empty;
    _handler?.ClearCache();

    SessionState.SetSession(sessionData);
    // 桥接（spec 4.3/M2/S3）：仅 Full 模式写全局 Scoped 实例（语义=当前活跃窗口）。
    // 主从交换路径（不重载会话）的补写见 OnParametersSetAsync 的 ViewMode 分支。
    if (ViewMode == SessionWindowMode.Full)
        _globalSessionState?.SetSession(sessionData);
    _renderedTitle = sessionData.Title;

    ResetTimelineFromSession();
    try { await EnsureDefaultAgentSelectionAsync(); } catch { }
    SyncContextState();
    ShowEmptyState = false;
    await ReconcileIncompleteTasksAsync();
    UpdateBudgetStatus();
    Context.NotifySessionLoaded(); // Session.razor 订阅后写 AppState.CurrentSessionId
}

// 事件桥接（迁移自 OnHandlerChanged，改造点：执行态清理保留在窗口层）
private async Task OnHandlerChanged(IMessageEvent evt)
{
    if (evt == null) return;
    // 会话归属过滤已由 _sync 内部处理；先分派时间线/状态
    _sync.ProcessEvent(evt);

    // 执行态清理（必改项 6）：ExecutionComplete 时按 handler 执行态清 SessionState，
    // 否则 IsExecuting 恒为 true、输入区卡死（原 OnHandlerChanged 分支）。
    // 排队场景 exec2 仍在跑时 handler.IsStreamActive=true，不清执行态（沿用已知边界）。
    if (evt is ExecutionCompleteEvent completeEvt && _handler != null && !_handler.IsStreamActive)
    {
        SessionState.ExecutionStatus = completeEvt.Status;
        SessionState.CurrentExecutionId = null;
        SessionState.CompleteExecution();
        SyncContextState();
    }

    // 导航副作用留在窗口 razor 层
    if (evt is NavigateEvent navEvt && !string.IsNullOrEmpty(navEvt.Target))
        NavigationManager.NavigateTo(navEvt.Target);
    else if (evt is CommandResultEvent commandEvt && !string.IsNullOrEmpty(commandEvt.NavigationTarget))
        NavigationManager.NavigateTo(commandEvt.NavigationTarget);
    else if (evt is CompactionFailedEvent cfe && !string.IsNullOrEmpty(cfe.ErrorMessage))
        MessageService.Error($"压缩失败: {cfe.ErrorMessage}");

    await InvokeAsync(StateHasChanged);
}

// 任务卡片聚合回调（迁移自 OnAggregatorAssistantChanged，加会话过滤）
private void OnAggregatorAssistantChanged(SessionMessage msg)
{
    // 建议项 2：仅当本窗口 == 聚合绑定目标时才同步。
    // 大屏下 root aggregator 供 active 窗口渲染 TaskSteps；子窗口（Summary/只读）不接收。
    var aggId = AggregatorSessionId ?? SessionId;
    if (!string.Equals(SessionId, aggId, StringComparison.Ordinal))
        return;
    Timeline.SyncAssistantMessage(msg, SessionId);
    _ = InvokeAsync(StateHasChanged);
}

// 时间线快捷方法委托给 _sync
private void ResetTimelineFromSession() => _sync.ResetFromSession();
private void ReconcileTimelineFromSession() => _sync.ReconcileAppendFromSession();
private void CompleteCurrentTimelineTurn() => _sync.CompleteTurn();
private void SyncTimelineAssistantFromStream(bool isComplete) => _sync.SyncAssistant(isComplete);

// 事件流绑定（迁移自 EnsureStreamBinding，改造点：handler 恒绑定；aggregator 走独立方法）
private void EnsureStreamBinding(string sessionId)
{
    if (string.IsNullOrEmpty(sessionId))
        return;

    if (string.Equals(_boundSessionId, sessionId, StringComparison.Ordinal) && _handler != null)
        return;

    // 解绑旧会话（handler）
    if (_handler != null)
    {
        if (_onHandlerChangedCallback != null)
        {
            _handler.OnStateChanged -= _onHandlerChangedCallback;
            _onHandlerChangedCallback = null;
        }
        Router.DetachConsumer(_handler.SessionId, _handler);
    }

    var circuitId = CircuitContext.Id ?? "";
    _handler = Router.GetOrCreateConsumer<Services.EventStreamHandler>(sessionId, circuitId);
    _handler.ClearCache();
    _onHandlerChangedCallback = evt => _ = OnHandlerChanged(evt);
    _handler.OnStateChanged += _onHandlerChangedCallback;
    Router.AttachConsumer(sessionId, _handler);

    var overview = ChatOrchestrator.GetOverview(sessionId);
    if (overview?.CurrentExecution != null && !overview.CurrentExecution.IsTerminal)
        _handler.RestoreExecution(overview.CurrentExecution.ExecutionId, overview.CurrentExecution.Status);

    EnsureAggregatorBinding();

    _boundSessionId = sessionId;
}

// aggregator 绑定（spec 5.3 / B1）：仅 Full 模式且绑定目标变化时 Rebind。
// 多窗口共享 circuit 单实例 aggregator；目标恒为 AggregatorSessionId（大屏=root），
// Summary 窗口不触碰，避免频繁主从切换反复清态重扫（TaskCardAggregator.Rebind 会 Detach 旧父+清空 _tasks）。
// 独立方法：Summary→Full 切换（EnsureStreamBinding 提前 return 不会执行）时经 OnParametersSetAsync 单独激活。
private void EnsureAggregatorBinding()
{
    if (ViewMode != SessionWindowMode.Full)
        return;

    var circuitId = CircuitContext.Id ?? "";
    var aggId = AggregatorSessionId ?? SessionId;
    _aggregator = Router.GetOrCreateCircuitConsumer<Services.TaskCardAggregator>(circuitId);
    if (!string.Equals(_aggregator.ParentSessionId, aggId, StringComparison.Ordinal))
    {
        _aggregator.Rebind(aggId);
        _aggregator.Reconcile(aggId);
    }
    // 幂等订阅：先退订再订阅，避免多次调用累积重复回调
    _aggregator.AssistantChanged -= OnAggregatorAssistantChanged;
    _aggregator.AssistantChanged += OnAggregatorAssistantChanged;
}

// 标题去重渲染（_sync 状态回调）
private void ApplyTitleToUi(string? newTitle)
{
    if (string.IsNullOrEmpty(newTitle)) return;
    if (string.Equals(_renderedTitle, newTitle, StringComparison.Ordinal)) return;
    _renderedTitle = newTitle;
    _ = InvokeAsync(StateHasChanged);
}

// 刷新 SessionWindowContext 快照（替代原 AppState.NotifyChange）
private void SyncContextState()
{
    var s = SessionState.CurrentSession;
    Context.CurrentSession = s;
    Context.Title = s?.Title ?? "新会话";
    Context.IsSubAgentView = IsSubAgentView;
    Context.IsQueued = SessionState.IsQueued;
    Context.QueuePosition = SessionState.QueuePosition;
    Context.HasActiveExecution = SessionState.HasActiveExecution;
    Context.IsExecuting = SessionState.IsExecuting;
    Context.SelectedAgent = SessionState.SelectedAgent;
    Context.SelectedModel = SessionState.SelectedModel;
    Context.SelectedAcpMode = SessionState.SelectedAcpMode;
    Context.ExecutionStatus = SessionState.ExecutionStatus;
    Context.CurrentTodoList = SessionState.CurrentTodoList;
    Context.NotifyStateChanged();
    // 状态回流渲染自触发（计划复审 A 点）：Context 由 HeaderTemplate 消费，须触发窗口重渲染
    // 使外层头部/ACP 输入框读到新值（Session 页与 Conference 全部消费方覆盖；OnInitialized 期间调用无害）
    _ = InvokeAsync(StateHasChanged);
    // IsAcpPassthrough 需异步查询 AgentRegistry，另行刷新（见 RefreshIsAcpAsync）
    _ = RefreshIsAcpAsync();
}

// ACP 运行时判断（必改项 7）：异步查询后写 Context.IsAcpPassthrough（外层头部据此切换 ACP 输入框）
private async Task RefreshIsAcpAsync()
{
    var agentId = SessionState.CurrentSession?.SelectedAgent;
    if (string.IsNullOrWhiteSpace(agentId))
    {
        Context.IsAcpPassthrough = false;
        return;
    }
    var isAcp = false;
    try
    {
        var info = await AgentRegistry.GetAgentAsync(agentId);
        isAcp = info?.Runtime == Seeing.Agent.Abstractions.Agents.AgentRuntime.AcpPassthrough;
    }
    catch { /* 查询失败按非 ACP 处理 */ }
    if (Context.IsAcpPassthrough != isAcp)
    {
        Context.IsAcpPassthrough = isAcp;
        Context.NotifyStateChanged();
        _ = InvokeAsync(StateHasChanged); // 计划复审 A 点：异步完成后触发渲染，ACP 输入框即时出现
    }
}

// ACP 切换时清空模型（保留原语义）：OnAgentChanged 中调用
private async Task OnAgentChanged(string value)
{
    var wasAcp = Context.IsAcpPassthrough;
    SessionState.SelectedAgent = value;
    if (SessionState.CurrentSession != null)
    {
        SessionState.CurrentSession.SelectedAgent = value;
        SessionState.CurrentSession.UpdatedAt = DateTime.Now;
        await SessionManager.SaveAsync(SessionState.CurrentSession.Id);
    }
    await RefreshIsAcpAsync();
    if (!wasAcp && Context.IsAcpPassthrough)
    {
        SessionState.SelectedModel = "";
        SessionState.SelectedAcpMode = "";
        if (SessionState.CurrentSession != null)
        {
            SessionState.CurrentSession.SelectedModel = "";
            SessionState.CurrentSession.SelectedAcpMode = "";
        }
    }
    SyncContextState();
}

// Dispose（迁移自 Session.razor:2089-2119，追加 SessionState.Dispose）
public void Dispose()
{
    SessionState.CancelExecution();
    _budgetSubscription?.Dispose();
    _sessionEventSubscription?.Dispose();
    _seeingAgentOptionsSubscription?.Dispose();
    if (_handler != null)
    {
        if (_onHandlerChangedCallback != null)
        {
            _handler.OnStateChanged -= _onHandlerChangedCallback;
            _onHandlerChangedCallback = null;
        }
        Router.DetachConsumer(_handler.SessionId, _handler);
    }
    if (_aggregator != null)
        _aggregator.AssistantChanged -= OnAggregatorAssistantChanged;
    SessionState.Dispose();
    // 沿用 2026-08-27 原则：不取消后端执行 / 不标记未完成任务
}
```

**Summary 模式**：`OnParametersSetAsync` 中当 `ViewMode` 变化时：进入 Summary 不订阅 budget（避免冗余）；handler 绑定始终保留（数据完整）。Summary 渲染依赖 `_sync`/SessionState（标题、状态、`GetLatestAssistantPreview()` 取最新 assistant 消息内容或运行中工具名）。点击回调：
```csharp
private async Task OnTileClick()
{
    if (OnClick.HasDelegate)
        await OnClick.InvokeAsync(SessionId);
}
```

### Step 3: 重构 `Session.razor`

- 移除已迁移的 @code 方法与字段（保留：选择器/选项加载/头部/移动端/对话框/导航）。
- `@inject SessionState SessionState` 改为注入全局实例并下传：`@inject Services.SessionState GlobalSessionState`（保留原 DI 注册）。
- 页面主体改为：

```razor
<SessionWindow SessionId="@SessionId"
               ViewMode="SessionWindowMode.Full"
               AggregatorSessionId="@SessionId"
               GlobalSessionState="@GlobalSessionState"
               Header="HeaderTemplate">
</SessionWindow>
```

- `HeaderTemplate` 渲染原头部 UI（标题/badge/选择器/操作按钮），经 `Context` 读写：
  - 标题：`Context.Title`；badge：`Context.IsQueued`/`Context.QueuePosition`/`Context.HasActiveExecution`/`Context.IsSubAgentView`
  - Agent 选择器：`Value="@Context.SelectedAgent"`，`ValueChanged="v => Context.SetAgentAsync!(v)"`
  - Model 选择器：`Value="@Context.SelectedModel"`，`ValueChanged="v => Context.SetModelAsync!(v)"`
  - **ACP 输入框显示（必改项 10）**：`@if (Context.IsAcpPassthrough)` 显示 Mode/Model 输入框（`Context.SelectedAcpMode`/`Context.SetAcpMode`），`else` 显示 Model 下拉——原 `_isAcpPassthrough` 页面字段由 `Context.IsAcpPassthrough` 取代，窗口加载/agent 变化时经 `SyncContextState`/`RefreshIsAcpAsync` 刷新
  - 重命名：对话框确认后 `await Context.RenameAsync!(title)`
  - 分支/清空/返回父会话/出站绑定：调 `Context.BranchAsync!()`/`Context.ClearAsync!()`/`Context.ReturnToParent!()`/直接读写 `Context.CurrentSession`
- 订阅 `Context.SessionLoaded`（OnInitialized 中）：
```csharp
_contextSubscription = ... // Session.razor 持有 Context 引用后：
// Context.SessionLoaded += () => { AppState.CurrentSessionId = Context.SessionId; _ = InvokeAsync(StateHasChanged); };
```
  > 因 `Context` 由子组件创建，`Session.razor` 需在子组件 `OnInitialized` 之后才能访问。实现方式：Session.razor 在 `OnAfterRender` 首次渲染后检查 `_windowRef`（`@ref`）并订阅其 `Context.SessionLoaded`；或由 SessionWindow 在加载完成后经 `Context.NotifySessionLoaded()` 触发，Session.razor 用 `@bind`/ref 方式建立订阅（详见实施时验证）。**简化替代**：Session.razor 在 Header 模板渲染时读取 `Context.CurrentSession?.Id`，若匹配则写 `AppState.CurrentSessionId`（幂等，无需事件）。**推荐简化替代**，避免 ref/事件时序复杂度。

- 保留 `LoadOptionsAsync`（选项列表 `_agentNames`/`_models` 供头部 Select）。
- 保留重命名/出站对话框、移动端溢出面板。
- **选择器值同步（必改项 10）**：Session.razor 不再持有 `_selectedAgent/_selectedModel/_isAcpPassthrough` 写路径；Select 直接单向绑定 `Context.SelectedAgent/SelectedModel`，值变更经 `Context.SetAgentAsync/SetModelAsync` 写回会话并经 `SyncContextState` 回流。`_modelInvalid` 发送校验迁移到窗口 `OnSubmitMessage`（沿用原逻辑）。

### Step 4: 构建 + 手工回归

```bash
dotnet build Seeing.Agent.slnx
```
手工验证 `/session/{id}`：加载、发送消息、流式渲染、取消、任务卡片、Todo、压缩、重命名、清空、分支、子会话只读、`SecurityPage` 显示当前会话 agent。

### Step 5: 提交

```bash
git add samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor samples/Seeing.Agent.WebUI/Pages/Session.razor
git commit -m "refactor(webui): 抽取 SessionWindow 组件，Session.razor 瘦身为头部+窗口"
```

---

## Task 7: `ConferenceRegistry` 子会话窗口注册表

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/ConferenceRegistry.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`（AddScoped 注册）
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/ConferenceRegistryTests.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/Seeing.Agent.WebUI.Tests/Services/ConferenceRegistryTests.cs`：

```csharp
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class ConferenceRegistryTests
{
    private static SessionData CreateChild(string childId, string parentId, string originToolCallId)
    {
        var child = SessionData.Create("p1", "explore");
        child.Id = childId;
        child.Kind = SessionKind.SubAgent;
        child.ParentSessionId = parentId;
        child.Metadata[SessionMetadataKeys.OriginToolCallId] = originToolCallId;
        return child;
    }

    private static SessionEventStreamRouter CreateRouter(Mock<IChatOrchestrator> orchestrator)
        => new(orchestrator.Object, Mock.Of<IServiceScopeFactory>(), NullLogger<SessionEventStreamRouter>.Instance);

    private static Mock<IChatOrchestrator> CreateOrchestratorMock(
        Dictionary<string, Channel<IMessageEvent>> channels)
    {
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.GetBufferedEvents(It.IsAny<string>())).Returns(new List<IMessageEvent>());
        orchestrator.Setup(o => o.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string sessionId, CancellationToken _) => channels[sessionId].Reader.ReadAllAsync());
        return orchestrator;
    }

    [Fact]
    public async Task Rebind_ShouldEnumerateChildrenIntoWindows()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        registry.Rebind(parentId);
        await Task.Delay(200);

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
    }

    [Fact]
    public async Task OnEvent_TaskToolCall_ShouldAddNewWindowAndRaiseChanged()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        // 初始枚举返回空（模拟子会话尚未在缓存/磁盘出现），验证"动态识别"路径独立生效（建议项 4）
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        // 动态路径经 TaskSessionResolver → ListChildrenAsync 兜底：此处允许在动态事件到来时命中
        // （与 Rebind 枚举区分：AttachFromToolCallAsync 走 ResolveTaskIdAsync，同样查询 ListChildrenAsync）
        sm.SetupSequence(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        var changed = 0;
        registry.WindowsChanged += () => changed++;
        registry.Rebind(parentId);

        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(300);

        changed.Should().BeGreaterThan(0);
        registry.Windows.Should().Contain(w => w.SessionId == "child1");
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~ConferenceRegistryTests"
```
Expected: 编译失败（类不存在）。

- [ ] **Step 3: 实现 `ConferenceRegistry`**

创建 `samples/Seeing.Agent.WebUI/Services/ConferenceRegistry.cs`：

```csharp
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 子会话窗口注册表（circuit 维度 IStreamConsumer，经 Router.GetOrCreateCircuitConsumer 获取/登记）。
/// 订阅主会话父流：初始枚举 SubAgent 子会话 + 动态识别 task 工具调用新增窗口。
/// 只维护窗口集合（SessionId/ParentId/Kind/Title），执行状态由各窗口自身 handler 提供（spec B2）。
/// 完成保留：子会话终态不影响窗口集合。
/// </summary>
public sealed class ConferenceRegistry : IStreamConsumer
{
    /// <summary>窗口树节点（本期为单 root + 一级子会话，DTO 为后续树扩展留路）</summary>
    public sealed record WindowNode(string SessionId, string? ParentId, SessionKind Kind, string Title);

    private readonly SessionEventStreamRouter _router;
    private readonly ISessionManager _sessionManager;
    private readonly TaskSessionResolver _taskResolver;
    private readonly List<WindowNode> _windows = new();
    private readonly object _lock = new();

    public string SessionId => ParentSessionId ?? string.Empty;
    public string? ParentSessionId { get; private set; }
    public IReadOnlyList<WindowNode> Windows { get { lock (_lock) return _windows.ToArray(); } }

    /// <summary>窗口集合变化（初始枚举/动态新增/Rebind）后触发</summary>
    public event Action? WindowsChanged;

    public ConferenceRegistry(
        SessionEventStreamRouter router,
        ISessionManager sessionManager,
        TaskSessionResolver taskResolver)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _taskResolver = taskResolver ?? throw new ArgumentNullException(nameof(taskResolver));
    }

    /// <summary>绑定主会话：摘除旧父订阅、清空集合、重新枚举并挂新父（同 circuit 换主会话路由时调用）</summary>
    public void Rebind(string mainId)
    {
        if (!string.IsNullOrEmpty(ParentSessionId)
            && !string.Equals(ParentSessionId, mainId, StringComparison.Ordinal))
            _router.DetachConsumer(ParentSessionId, this);

        lock (_lock)
            _windows.Clear();

        ParentSessionId = mainId;
        _ = EnumerateAsync(mainId);
        _router.AttachConsumer(mainId, this);
    }

    /// <summary>初始/刷新枚举：内存缓存 → 磁盘冷缓存兜底。捕获 parentId 做竞态防护（连续 Rebind 时旧枚举晚到不污染新集合）</summary>
    private async Task EnumerateAsync(string parentId)
    {
        try
        {
            var children = await _sessionManager.ListChildrenAsync(parentId, SessionKind.SubAgent);
            AddChildren(parentId, children);

            var diskChildren = await _sessionManager.LoadChildrenFromStorageAsync(parentId);
            AddChildren(parentId, diskChildren);
        }
        catch
        {
            // 枚举失败不阻断订阅（后续动态事件仍可补窗）
        }
    }

    private void AddChildren(string parentId, IReadOnlyList<SessionData>? children)
    {
        if (children == null)
            return;

        lock (_lock)
        {
            // 竞态防护（建议项 3）：连续 Rebind 后旧枚举晚到，ParentSessionId 已变 → 丢弃
            if (!string.Equals(ParentSessionId, parentId, StringComparison.Ordinal))
                return;

            var added = false;
            foreach (var c in children)
            {
                if (_windows.Any(w => string.Equals(w.SessionId, c.Id, StringComparison.Ordinal)))
                    continue;
                _windows.Add(new WindowNode(c.Id, c.ParentSessionId, c.Kind, c.Title ?? string.Empty));
                added = true;
            }
            if (added)
                WindowsChanged?.Invoke();
        }
    }

    public void OnEvent(IMessageEvent evt)
    {
        if (evt == null || string.IsNullOrEmpty(evt.SessionId))
            return;

        // 仅处理父流中的 task 工具调用
        if (evt is not ToolCallEvent tool || !IsTaskToolName(tool.ToolName))
            return;

        _ = AttachFromToolCallAsync(tool);
    }

    public void OnStreamEnd()
    {
        // 无清理需求（窗口集合由 Rebind/Dispose 管理）
    }

    private async Task AttachFromToolCallAsync(ToolCallEvent tool)
    {
        try
        {
            var parentId = ParentSessionId;
            if (string.IsNullOrEmpty(parentId))
                return;

            var taskId = await _taskResolver.ResolveTaskIdAsync(parentId,
                new SessionToolCall { Id = tool.ToolCallId });  // ToolCallEvent 无 TaskId 属性（必改项 8）
            if (string.IsNullOrEmpty(taskId))
                return;

            // 竞态防护（计划复审建议 1）：await 期间 Rebind 换父后，旧任务晚到不得向新集合添加
            if (!string.Equals(ParentSessionId, parentId, StringComparison.Ordinal))
                return;

            lock (_lock)
            {
                if (_windows.Any(w => string.Equals(w.SessionId, taskId, StringComparison.Ordinal)))
                    return;
                var child = _sessionManager.Get(taskId);
                _windows.Add(new WindowNode(taskId, parentId, SessionKind.SubAgent, child?.Title ?? string.Empty));
            }
            WindowsChanged?.Invoke();
        }
        catch
        {
            // 挂载失败不阻断事件流（消费者异常隔离）
        }
    }

    private static bool IsTaskToolName(string? name) =>
        string.Equals(name, "task", StringComparison.OrdinalIgnoreCase);
}
```

> 说明：`ToolCallEvent.TaskId` 若不存在，用 `ResolveTaskIdAsync` 解析；`TaskId` 解析依赖 `origin_tool_call_id`（父会话创建子会话时写入）。

- [ ] **Step 4: Program.cs 注册**

在 `Program.cs`（`AddScoped<TaskSessionResolver>()` 附近）：
```csharp
builder.Services.AddScoped<ConferenceRegistry>();
```

- [ ] **Step 5: 运行测试**

```bash
dotnet build tests/Seeing.Agent.WebUI.Tests
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~ConferenceRegistryTests"
```
Expected: 全部 PASS。

- [ ] **Step 6: 整体构建**

```bash
dotnet build Seeing.Agent.slnx
```

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/ConferenceRegistry.cs samples/Seeing.Agent.WebUI/Program.cs tests/Seeing.Agent.WebUI.Tests/Services/ConferenceRegistryTests.cs
git commit -m "feat(webui): 新增 ConferenceRegistry 子会话窗口注册表"
```

---

## Task 8: `Conference.razor` 大屏页 + 入口

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Pages/Conference.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`（头部加"大屏"入口按钮）

- [ ] **Step 1: 创建 `Conference.razor`**

```razor
@page "/conference"
@page "/conference/{SessionId}"
@using AntDesign
@using Seeing.Agent.WebUI.Services
@using Seeing.Agent.WebUI.State
@using Seeing.Session.Core
@inject SessionEventStreamRouter Router
@inject CircuitContext CircuitContext
@inject NavigationManager NavigationManager
@inject ISessionManager SessionManager
@inject SessionState GlobalSessionState
@implements IDisposable

<div class="conference-page">
    <div class="conference-topbar">
        <Button Type="@ButtonType.Text" Size="@ButtonSize.Small" Icon="@IconType.Outline.ArrowLeft"
                OnClick="@GoToSession">返回会话页</Button>
        <span class="conference-main-title">@GetMainTitle()</span>
        <Badge Status="@BadgeStatus.Processing" Text="实时" />
    </div>

    @if (_mainMissing)
    {
        @* 建议项 9：主会话缺失（URL 指定但会话已删）→ 错误态 + 返回会话列表 *@
        <Alert Type="@AlertType.Error"
               Message="主会话不存在或已删除"
               ShowIcon="true"
               Class="conference-main-missing">
            <Button Type="@ButtonType.Link" Size="@ButtonSize.Small" OnClick="@GoToSessionList">返回会话列表</Button>
        </Alert>
    }
    else
    {
        @* 单网格容器：所有窗口 @key=sessionId 稳定实例，主从交换仅变 ViewMode 参数（spec 5.1） *@
        <div class="conference-grid">
            @foreach (var node in AllWindows)
            {
                var isActive = string.Equals(node.SessionId, _activeSessionId, StringComparison.Ordinal);
                <ErrorBoundary>
                    <SessionWindow @key="@node.SessionId"
                                   SessionId="@node.SessionId"
                                   ViewMode="@(isActive ? SessionWindowMode.Full : SessionWindowMode.Summary)"
                                   AggregatorSessionId="@_mainSessionId"
                                   GlobalSessionState="@GlobalSessionState"
                                   OnClick="@OnTileClick" />
                </ErrorBoundary>
            }
        </div>
    }
</div>

<style>
    .conference-grid {
        display: grid;
        grid-template-columns: repeat(6, 1fr);
        grid-auto-rows: minmax(180px, auto);
        gap: 8px;
        padding: 8px;
        flex: 1;
        overflow: auto;
    }
    .conference-grid .session-window--full { grid-column: span 4; grid-row: span 3; }
    .conference-grid .session-window--summary { grid-column: span 2; grid-row: span 1; }
</style>

@code {
    [Parameter] public string? SessionId { get; set; }

    private string? _mainSessionId;
    private string? _activeSessionId;
    private ConferenceRegistry? _registry;
    private IReadOnlyList<ConferenceRegistry.WindowNode> _windows = Array.Empty<ConferenceRegistry.WindowNode>();
    private bool _mainMissing;
    private bool _loaded;

    /// <summary>全部窗口：主会话（root）+ 子会话（registry.Windows）</summary>
    private IReadOnlyList<ConferenceRegistry.WindowNode> AllWindows
    {
        get
        {
            var nodes = new List<ConferenceRegistry.WindowNode>();
            if (!string.IsNullOrEmpty(_mainSessionId))
            {
                var main = SessionManager.Get(_mainSessionId);
                nodes.Add(new ConferenceRegistry.WindowNode(_mainSessionId, null, SessionKind.Root, main?.Title ?? string.Empty));
            }
            nodes.AddRange(_windows);
            return nodes;
        }
    }

    private async Task OnTileClick(string sessionId)
    {
        _activeSessionId = sessionId;
        await InvokeAsync(StateHasChanged);
    }

    private void GoToSession()
    {
        if (!string.IsNullOrEmpty(_activeSessionId))
            NavigationManager.NavigateTo($"/session/{_activeSessionId}");
    }

    private void GoToSessionList() => NavigationManager.NavigateTo("/session");

    private string GetMainTitle()
    {
        var session = SessionManager.Get(_mainSessionId ?? "");
        return session?.Title ?? "会议大屏";
    }

    protected override async Task OnInitializedAsync()
    {
        var mainId = !string.IsNullOrEmpty(SessionId) ? SessionId
            : (await SessionManager.LoadAllFromStorageAsync()).FirstOrDefault()?.Id;
        if (string.IsNullOrEmpty(mainId))
        {
            _mainMissing = true;
            return;
        }

        // 建议项 9：URL 指定但主会话已删除 → 错误态（缓存 → 存储兜底确认）
        var main = SessionManager.Get(mainId);
        if (main == null)
        {
            main = await SessionManager.LoadAsync(mainId);
            if (main == null)
            {
                _mainMissing = true;
                return;
            }
        }

        _mainSessionId = mainId;
        _activeSessionId = mainId;
        var circuitId = CircuitContext.Id ?? "";

        _registry = Router.GetOrCreateCircuitConsumer<ConferenceRegistry>(circuitId);
        _registry.Rebind(mainId);
        _registry.WindowsChanged += OnWindowsChanged;

        await InvokeAsync(StateHasChanged);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrEmpty(SessionId) && !string.Equals(SessionId, _mainSessionId, StringComparison.Ordinal))
        {
            _mainSessionId = SessionId;
            _activeSessionId = SessionId;

            // 计划复审建议 3：换路由重新校验主会话（缓存 → 存储兜底），重置错误态，
            // 避免无效 id 置 _mainMissing 后同页改有效 id 错误态滞留
            var main = SessionManager.Get(SessionId);
            if (main == null)
            {
                main = await SessionManager.LoadAsync(SessionId);
                if (main == null)
                {
                    _mainMissing = true;
                    await InvokeAsync(StateHasChanged);
                    return;
                }
            }
            _mainMissing = false;

            _registry?.Rebind(SessionId);
            await InvokeAsync(StateHasChanged);
        }
    }

    private void OnWindowsChanged()
    {
        _windows = _registry?.Windows ?? Array.Empty<ConferenceRegistry.WindowNode>();
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        if (_registry != null)
            _registry.WindowsChanged -= OnWindowsChanged;
        // circuit 关闭由 SeeingCircuitHandler → Router.DetachAllForCircuit 统一释放
    }
}
```

> **说明**：
> - **单网格布局关键设计**：主窗口与全部子窗口渲染在**同一 `.conference-grid` 容器**，每个窗口 `@key=sessionId`。主从交换只改 `_activeSessionId` → 各窗口 `ViewMode` 参数变化（active=Full 占大格，其余=Summary 占小格），**组件实例永不销毁/重建**，handler/timeline store 保留、无重复订阅（规避"双容器"方案下同一会话被两个窗口实例绑定的缺陷）。
> - `SessionWindow` 需在 `OnParametersSetAsync` 感知 `ViewMode` 变化：进入 Full 激活输入区/budget/压缩/Todo 订阅，进入 Summary 停用（保留 handler/timeline）。
> - `GlobalSessionState` 由 `@inject SessionState GlobalSessionState` 提供（保持 DI Scoped 注入），传给各窗口做桥接写入（spec S3）。
> - `AggregatorSessionId` 恒为 `_mainSessionId`（root）：仅 Full 窗口按条件 Rebind（Task 6），Summary 窗口不触碰。
> - 布局样式用 CSS Grid：Full 占 `span 4/3`，Summary 占 `span 2/1`，构成"会议"网格。`.session-window--full/--summary` 由 `SessionWindow` 根元素 class 提供（Task 6）。

- [ ] **Step 2: Session.razor 头部加大屏入口**

在 `Session.razor` 头部工具条（`session-header-right` 的 `Space` 内，新建按钮前）加：

```razor
<SpaceItem>
    <Tooltip Title="多 Agent 会议大屏">
        <Button Type="@ButtonType.Text"
                Size="@ButtonSize.Small"
                Icon="@IconType.Outline.VideoCamera"
                OnClick="@GoToConference" />
    </Tooltip>
</SpaceItem>
```

@code 新增：
```csharp
private void GoToConference()
{
    // Task 6 后 Session.razor 注入为 GlobalSessionState（全局 Scoped 实例，窗口桥接已写入当前会话）
    var id = GlobalSessionState.CurrentSession?.Id;
    if (string.IsNullOrEmpty(id)) return;
    NavigationManager.NavigateTo($"/conference/{id}");
}
```

- [ ] **Step 3: 构建**

```bash
dotnet build Seeing.Agent.slnx
```
Expected: 无编译错误。

- [ ] **Step 4: 手工验证（无自动化覆盖，spec 明确边界）**

1. 从 `/session/{id}` 点大屏按钮进入 `/conference/{id}`
2. 主窗口显示主会话，可发送消息、流式渲染
3. 主 agent 发起 task 工具调用 → 小窗自动出现
4. 点击小窗 → 主从交换；active 为子会话时只读、可点回主会话
5. 主会话执行中刷新页面 → 窗口集合重新枚举恢复
6. 各窗口独立实时更新（小窗摘要、主窗完整）

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/Conference.razor samples/Seeing.Agent.WebUI/Pages/Session.razor
git commit -m "feat(webui): 新增多 Agent 会议大屏页与入口"
```

---

## Self-Review

### Spec 覆盖核对
- 1.2 目标 1-5 → Task 6/8（会议页/主从交换/实时渲染）、Task 1/2/7（基础设施）
- 3.3 Router 泛化 → Task 2
- 4.1 `AggregatorSessionId` → Task 6/8
- 4.2/5.3 aggregator 归属（B1）→ Task 6（`AggregatorSessionId ?? SessionId`）
- 4.3 状态实例化/桥接/`SessionState.IDisposable`/AppState → Task 5/6
- 4.4 Context（含 `QueuePosition`/`SessionLoaded`）→ Task 6
- 4.6 选项加载归属 → Task 6（`LoadOptionsAsync` 留 Session.razor）
- 6.1 TimelineSync 边界（S6 标题落点）→ Task 3/6
- 6.2 ConferenceRegistry（树节点、Rebind、try/catch、B2 无 ExecutionStatus）→ Task 7
- 6.3 TaskSessionResolver 注册（S2）→ Task 1
- 7 生命周期/错误处理（Dispose 原则、ErrorBoundary、S1 MessageList 边界）→ Task 4/6/8
- 8 测试 → 各任务
- 10 可扩展性 → Task 2（多实例键）/7（树节点）铺垫

### Placeholder 检查
- Task 6 中 `SessionWindow.razor` 的完整 @code 以"迁移映射表 + 关键片段"给出，其余代码**从现有 Session.razor 复制**（非占位符；源文件行号已注明）。
- Task 3 测试对事件属性名的断言标注了"以 Abstractions/Events 实际定义为准"的修正指引（编译错误驱动）。

### 类型一致性
- `GetOrCreateCircuitConsumer<T>(circuitId, string? key = null)` 在 Task 2 定义、Task 7（ConferenceRegistry 经此获取）与 Task 8 使用一致。
- `SessionWindowContext` 的 `SetAgentAsync/SetModelAsync/SetAcpMode/RenameAsync/BranchAsync/ClearAsync/ReturnToParent/GetWorkspace` 在 Task 6 定义、Task 8 Header 未用（顶部条独立）、Session.razor 使用一致。
- `SessionWindow` 参数（`SessionId/ViewMode/Header/OnClick/AggregatorSessionId/GlobalSessionState`）在 Task 6 定义、Task 8 使用一致。
- `ConferenceRegistry.WindowNode(SessionId, ParentId, Kind, Title)` 在 Task 7 定义、Task 8 使用一致。
- `TaskSessionResolver.ResolveTaskIdAsync(parentSessionId, toolCall)` 在 Task 1 定义、Task 7 使用一致。

### 依赖顺序
Task 1 → Task 2 → Task 3 → Task 4 → Task 5 → Task 6 → Task 7 → Task 8（Task 7/8 依赖 Task 2 泛化与 Task 1 resolver；Task 6 依赖 Task 2/3/4/5）。

---

## 计划审查落实（子 agent 审查，2026-08-28）

计划经独立子 agent 审查，结论 **有条件通过**（11 必改 + 10 建议）。以下全部落实（无遗留）：

| # | 审查项 | 落实位置 |
|---|--------|----------|
| 1 | `SessionEventStreamRouterTests:309` 二参构造漏改 | Task 1 Step 5(b) |
| 2 | Task 2 新测试 scopeFactory 缺失 → NRE | Task 2 Step 1（`CreateScopeFactoryFor<T>` + `new()` 无参构造） |
| 3 | Task 3 StreamDelta 测试节流阈值必失败 | Task 3 Step 1（含 `\n` 触发强制刷新 + 模拟服务端写入） |
| 4 | Task 6 SessionWindow `@using` 缺失 | Task 6 Step 2（补齐 `Events/Configuration/Llm/Tools/Skills/Core`） |
| 5 | Task 6 映射表不完整 + `LoadCommandsFromBackend` 矛盾 + `_compaction*` 迁移 | Task 6 Step 1（补窗口必需方法、命令归窗口、`_sync.Compaction*`） |
| 6 | Task 6 执行态清理缺失（IsExecuting 恒 true） | Task 6 OnHandlerChanged 关键片段 |
| 7 | Task 6 `EnsureDefaultAgentSelectionAsync/OnAgentChanged` 依赖页面字段 | Task 6 "选择器适配" + `RefreshIsAcpAsync` + `Context.IsAcpPassthrough` |
| 8 | Task 7 `tool.TaskId` 不存在 | Task 7 Step 3（`new SessionToolCall { Id = tool.ToolCallId }`） |
| 9 | Task 8 `GoToConference` 标识符未定义 | Task 8 Step 2（`GlobalSessionState.CurrentSession?.Id`） |
| 10 | Task 6 全局桥接无条件写 + 选择器值同步 | Task 6（桥接仅 Full 写 + `OnParametersSetAsync` 进 Full 补写；Select 单向绑定 Context） |
| 11 | spec §8 测试缺口（TimelineStore 多实例 / SessionState Dispose） | Task 5 扩展 |
| S1 | 离开 Full 退订 budget | Task 6 `OnParametersSetAsync` else 分支 |
| S2 | `OnAggregatorAssistantChanged` 会话过滤 | Task 6 关键片段（`SessionId == AggregatorSessionId` 才同步） |
| S3 | `ConferenceRegistry.Rebind` 枚举竞态 | Task 7 Step 3（`AddChildren(parentId, ...)` 校验 `ParentSessionId`） |
| S4 | Task 7 动态新增测试恒真 | Task 7 Step 1（初始枚举 mock 空 + `SetupSequence`） |
| S5 | `SessionState.Dispose` 补释放 cts + 防重入 | Task 5 Step 3 |
| S6 | Conference 主会话缺失错误态 | Task 8（`_mainMissing` + Alert + `GoToSessionList`） |

已核对并确认：事件类型/属性名（`StreamDeltaEvent.ContentDelta`、`ExecutionCompleteEvent.Status`、`SessionTitleChangedEvent`、`Compaction*` 等）、命名空间（`Seeing.Agent.Events`/`Seeing.Agent.App.Events`）、关键 API（`TaskCardAggregator.ParentSessionId`、`ISessionManager.LoadAllFromStorageAsync`、`SessionMessage.UserMessage`）均与代码库一致。

### 计划复审（第二轮，2026-08-28）

复审结论 **需再修改**（1 必改 A + 4 建议），已全部落实：

| # | 复审项 | 落实位置 |
|---|--------|----------|
| A | Context 状态回流无渲染触发（`SyncContextState`/`RefreshIsAcpAsync` 不触发 `StateHasChanged`，HeaderTemplate 读不到新值） | Task 6：`SyncContextState` 末尾与 `RefreshIsAcpAsync` 变化分支各追加 `_ = InvokeAsync(StateHasChanged);` |
| B1 | `AttachFromToolCallAsync` 动态新增无 parentId 竞态校验 | Task 7：`await` 后校验 `ParentSessionId == parentId` |
| B2 | `SessionState.Dispose` 后并发 `CancelExecution` 操作已释放锁 | Task 5：`CancelExecution` 开头加 `if (_disposed) return;` |
| B3 | Conference 换路由不重置 `_mainMissing`，错误态滞留 | Task 8：`OnParametersSetAsync` 换路由重新校验（缓存 → 存储兜底）+ 重置标志 |
| B4 | `GetWorkspace`/`GetStatusBadge`/`GetLatestAssistantPreview` 无实现来源 | Task 6：映射表补"补充方法体要点" |
