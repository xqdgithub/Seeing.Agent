# 统一多流订阅基础设施 + Task 卡片 UI 层聚合 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立统一多流订阅基础设施（`SessionEventStreamRouter`），使父会话渲染（`EventStreamHandler` 多实例化）与子会话聚合（`TaskCardAggregator`）都基于它，修复"子代理执行中 Task 卡片无进度"问题，并为 Agent 大屏打基础。

**Architecture:** 后端最小改动（TaskTool 写子会话关联元数据、ChatOrchestrator 单例化、SessionManager 新增磁盘子会话枚举）；UI 层新增 Singleton `SessionEventStreamRouter`（每会话流订阅一次、广播多消费者、引用快照去重、circuit scope 管理）+ `IStreamConsumer` 策略 + `CircuitContext`；`EventStreamHandler` 解耦 `SessionState` 改为每会话实例（`OnStateChanged(evt)` 带事件）；`TaskCardAggregator` 每父会话一实例，父流识别 task 工具调用自动挂载子会话订阅并聚合 TaskSteps 落盘。

**Tech Stack:** C# / .NET 10 / Blazor Server / xUnit / Moq / FluentAssertions

---

## 文件结构

| 文件 | 动作 | 职责 |
|------|------|------|
| `src/Seeing.Session/Core/SessionMetadataKeys.cs` | 修改 | 新增 `OriginToolCallId` 常量 |
| `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs` | 修改 | 创建/续跑子会话后写关联元数据 |
| `src/Seeing.Agent.App/ChatOrchestrator.cs` | 修改 | 移除死字段 `_permissionChannel` |
| `src/Seeing.Agent.App/ChatOrchestratorServiceExtensions.cs` | 修改 | `AddSingleton<IChatOrchestrator>` + 注释 |
| `src/Seeing.Session/Core/ISessionManager.cs` | 修改 | 新增 `LoadChildrenFromStorageAsync` |
| `src/Seeing.Session/Management/SessionManager.cs` | 修改 | 实现 `LoadChildrenFromStorageAsync` |
| `samples/Seeing.Agent.WebUI/Services/IStreamConsumer.cs` | 创建 | 消费者策略接口 |
| `samples/Seeing.Agent.WebUI/Services/CircuitContext.cs` | 创建 | Scoped circuit.Id 载体 |
| `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs` | 创建 | Singleton 统一订阅路由器 |
| `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` | 修改 | 多实例化重构（解耦 SessionState） |
| `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs` | 创建 | 子会话聚合策略 |
| `samples/Seeing.Agent.WebUI/Pages/Session.razor` | 修改 | 经 Router 订阅/切换/清理 |
| `samples/Seeing.Agent.WebUI/Program.cs` | 修改 | DI 注册 Router/CircuitContext/Aggregator |
| `samples/Seeing.Agent.WebUI/Services/SeeingCircuitHandler.cs` | 修改 | `OnCircuitClosedAsync` 回调 Router 清理 |
| 测试文件 | 创建/修改 | 各任务对应测试 |

---

### Task 1: SessionMetadataKeys.OriginToolCallId + TaskTool 写关联元数据

**Files:**
- Modify: `src/Seeing.Session/Core/SessionMetadataKeys.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs`
- Test: `tests/Seeing.Session.Tests/SessionMetadataKeysTests.cs`（新增）、`tests/Seeing.Agent.Tests/Tools/TaskToolTests.cs`

- [ ] **Step 1: 在 `SessionMetadataKeys.cs` 添加常量**

`src/Seeing.Session/Core/SessionMetadataKeys.cs` 的 `SessionMetadataKeys` 类内（`InstructionFingerprints` 常量之后）添加：

```csharp
    /// <summary>
    /// 子代理会话关联的父会话工具调用 ID（task 工具写入，UI 据此精确匹配父子关联）。
    /// </summary>
    public const string OriginToolCallId = "origin_tool_call_id";
```

- [ ] **Step 2: 编写 TaskTool 元数据写入测试（红）**

在 `tests/Seeing.Agent.Tests/Tools/TaskToolTests.cs` 中添加测试。先读该文件末尾以了解 `TaskToolFixture` 结构（`fixture.Child` 是子会话、`fixture.SessionManager` 是 mock）。新增测试：

```csharp
    [Fact]
    public async Task ExecuteAsync_Foreground_ShouldWriteOriginToolCallIdToChildMetadata()
    {
        using var fixture = new TaskToolFixture(executor: BuildExecutor("final answer"));
        var tool = new TaskTool(
            NullLogger<TaskTool>.Instance,
            fixture.SessionManager.Object,
            fixture.AgentRegistry.Object,
            fixture.LoopScheduler.Object);
        var context = new ToolContext
        {
            SessionId = fixture.ParentId,
            CallId = "call-origin-1",
            Services = fixture.ToolProvider
        };

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth config",
                subagent_type = "explore"
            }),
            context);

        result.Success.Should().BeTrue();
        fixture.Child.Metadata.Should()
            .ContainKey(SessionMetadataKeys.OriginToolCallId)
            .WhoseValue.Should().Be("call-origin-1");
    }
```

确保文件顶部有 `using Seeing.Session.Core;`（已存在）。

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet build tests/Seeing.Agent.Tests -c Debug && dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecuteAsync_Foreground_ShouldWriteOriginToolCallIdToChildMetadata"`
Expected: FAIL（`Metadata` 不含 `origin_tool_call_id`）

- [ ] **Step 4: TaskTool 写元数据**

在 `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs` 的 `ExecuteAsync` 中，**两个分支都拿到 `session` 之后**（`context.MetadataSink?.SetMetadata(...)` 之后、`AddMessageAsync` 之前）插入：

```csharp
            // 关联元数据：写入子会话，UI 据此精确匹配"父 task 工具调用 ↔ 子会话"（多子代理并行）
            if (!session.Metadata.ContainsKey(SessionMetadataKeys.OriginToolCallId))
            {
                session.Metadata[SessionMetadataKeys.OriginToolCallId] = context.CallId ?? string.Empty;
                await _sessionManager.SaveAsync(session.Id);
            }
```

`TaskTool.cs` 顶部已有 `using Seeing.Session.Core;`（`SessionMetadataKeys` 同命名空间，无需新增 using）。

- [ ] **Step 5: 运行测试确认通过**

Run: 同上命令
Expected: PASS

- [ ] **Step 6: 回归 TaskTool 现有测试**

Run: `dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskToolTests"`
Expected: 全部 PASS（含前台/后台路径）

- [ ] **Step 7: 提交**

```bash
git add src/Seeing.Session/Core/SessionMetadataKeys.cs src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs tests/Seeing.Agent.Tests/Tools/TaskToolTests.cs
git commit -m "feat(task): TaskTool 写子会话关联元数据 origin_tool_call_id"
```

---

### Task 2: ChatOrchestrator 移除死字段并变 Singleton

**Files:**
- Modify: `src/Seeing.Agent.App/ChatOrchestrator.cs`
- Modify: `src/Seeing.Agent.App/ChatOrchestratorServiceExtensions.cs`
- Test: `tests/Seeing.Agent.App/ChatOrchestratorServiceExtensionsTests.cs`（新增）

- [ ] **Step 1: 编写注册测试（红）**

新增 `tests/Seeing.Agent.App/ChatOrchestratorServiceExtensionsTests.cs`（若目录不存在则创建）：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Seeing.Agent.App.Tests;

public class ChatOrchestratorServiceExtensionsTests
{
    [Fact]
    public void AddChatOrchestrator_ShouldRegisterSingleton()
    {
        var services = new ServiceCollection();
        services.AddChatOrchestrator();

        var descriptor = services.First(d => d.ServiceType == typeof(IChatOrchestrator));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }
}
```

若测试项目无 FluentAssertions，改用 xunit Assert：`Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);`。确认 `tests/Seeing.Agent.App` 项目文件存在（否则在现有 App 测试项目中新增该文件）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/Seeing.Agent.App -c Debug && dotnet vstest tests/Seeing.Agent.App/bin/Debug/net10.0/Seeing.Agent.App.dll --TestCaseFilter:"FullyQualifiedName~ChatOrchestratorServiceExtensionsTests"`
Expected: FAIL（Lifetime 为 Scoped）

- [ ] **Step 3: ChatOrchestrator 移除死字段**

`src/Seeing.Agent.App/ChatOrchestrator.cs`：
- 删除字段 `private readonly IPermissionChannel _permissionChannel;`（第 34 行）
- 从构造函数参数列表删除 `IPermissionChannel permissionChannel,`
- 从构造体删除 `_permissionChannel = permissionChannel;`
- 若 `IPermissionChannel` 不再被其他代码使用，删除 `using Seeing.Agent.Abstractions.Permissions;`（先确认文件内无其他 `IPermissionChannel` 引用）

- [ ] **Step 4: 注册改 Singleton + 注释**

`src/Seeing.Agent.App/ChatOrchestratorServiceExtensions.cs` 第 43-44 行：

```csharp
        // 注册 ChatOrchestrator (Singleton：移除对 Scoped IPermissionChannel 的依赖后全部依赖均为 Singleton)
        services.AddSingleton<IChatOrchestrator, ChatOrchestrator>();
```

- [ ] **Step 5: 运行确认通过**

Run: 同 Step 2 命令
Expected: PASS

- [ ] **Step 6: 编译验证无遗漏引用**

Run: `dotnet build src/Seeing.Agent.App -c Debug`
Expected: 成功（无编译错误；若有其他构造 ChatOrchestrator 的位置报错，一并移除 `_permissionChannel` 实参）

- [ ] **Step 7: 提交**

```bash
git add src/Seeing.Agent.App/ChatOrchestrator.cs src/Seeing.Agent.App/ChatOrchestratorServiceExtensions.cs tests/Seeing.Agent.App/
git commit -m "refactor(orchestrator): ChatOrchestrator 移除死字段并变 Singleton"
```

---

### Task 3: SessionManager.LoadChildrenFromStorageAsync

**Files:**
- Modify: `src/Seeing.Session/Core/ISessionManager.cs`
- Modify: `src/Seeing.Session/Management/SessionManager.cs`
- Test: `tests/Seeing.Session.Tests/SessionManagerStorageTests.cs`（新增或追加）

- [ ] **Step 1: 接口加方法**

`src/Seeing.Session/Core/ISessionManager.cs`，`LoadAllFromStorageAsync` 声明之后添加：

```csharp
        /// <summary>
        /// 从存储加载指定父会话的全部子会话（Kind=SubAgent）并注册到缓存。
        /// 用于冷缓存下 UI 关联子代理卡片；缓存已存在的会话跳过。
        /// </summary>
        Task<IReadOnlyList<SessionData>> LoadChildrenFromStorageAsync(
            string parentId, CancellationToken ct = default);
```

- [ ] **Step 2: 编写测试（红）**

新增 `tests/Seeing.Session.Tests/SessionManagerStorageTests.cs`，使用 InMemory 或 File 临时 store。参照现有 `tests/Seeing.Session.Tests/Management/SessionChildTests.cs` 的 fixture 模式：

```csharp
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests;

public class SessionManagerStorageTests
{
    private static SessionManager CreateManager(ISessionStore store)
    {
        // 参照现有测试的 SessionManager 构造方式（mock hook/archiver 或 null）
        return new SessionManager(store, null, null, null);
    }

    [Fact]
    public async Task LoadChildrenFromStorageAsync_ShouldLoadSubAgentChildrenFromDisk()
    {
        var store = new InMemorySessionStore();
        var manager = CreateManager(store);

        var parent = manager.Create(partitionId: "p1", selectedAgent: "general");
        parent.Kind = SessionKind.Root;
        await manager.SaveAsync(parent.Id);

        // 直接经 store 保存一个子会话（绕开缓存）
        var child = SessionData.Create("p1", "explore");
        child.Kind = SessionKind.SubAgent;
        child.ParentSessionId = parent.Id;
        child.Metadata[SessionMetadataKeys.OriginToolCallId] = "call-1";
        await store.SaveAsync(child);

        // 冷缓存：先断言 ListChildrenAsync 查不到
        manager.ListChildrenAsync(parent.Id, SessionKind.SubAgent).Result.Should().BeEmpty();

        var loaded = await manager.LoadChildrenFromStorageAsync(parent.Id);
        loaded.Should().ContainSingle(s => s.Id == child.Id);
        loaded[0].Metadata[SessionMetadataKeys.OriginToolCallId].Should().Be("call-1");
    }
}
```

先读 `tests/Seeing.Session.Tests` 现有测试，确认 `SessionManager` 构造签名与 store 类型，据此修正。若 `SessionManager` 构造有非空必需依赖（如 `ISessionArchiver`），用 Moq 或现有 helper 提供。

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test tests/Seeing.Session.Tests --filter "FullyQualifiedName~LoadChildrenFromStorageAsync_ShouldLoadSubAgentChildrenFromDisk"`
Expected: FAIL（编译失败：方法不存在）

- [ ] **Step 4: 实现方法**

`src/Seeing.Session/Management/SessionManager.cs`，在 `LoadAllFromStorageAsync` 之后添加：

```csharp
        /// <inheritdoc/>
        public async Task<IReadOnlyList<SessionData>> LoadChildrenFromStorageAsync(
            string parentId, CancellationToken ct = default)
        {
            if (_store == null)
            {
                _logger?.LogDebug("未配置存储，返回内存缓存中的子会话");
                return await ListChildrenAsync(parentId, SessionKind.SubAgent, ct);
            }

            var result = new List<SessionData>();
            var asyncEnumerable = await _store.ListAsync();
            await foreach (var session in asyncEnumerable.WithCancellation(ct))
            {
                if (!string.Equals(session.ParentSessionId, parentId, StringComparison.Ordinal))
                    continue;
                if (session.Kind != SessionKind.SubAgent)
                    continue;

                // 缓存已存在则跳过，避免磁盘旧快照覆盖执行中活跃子会话的内存态
                if (!_sessionDataCache.ContainsKey(session.Id))
                    _sessionDataCache[session.Id] = session;
                result.Add(_sessionDataCache[session.Id]);
            }

            _logger?.LogInformation("从存储加载父会话 {ParentId} 的子会话 {Count} 个", parentId, result.Count);
            return result;
        }
```

确认 `ListChildrenAsync` 签名是否含 `ct` 参数（若接口为 `(string parentId, SessionKind? kind = null, CancellationToken ct = default)` 则正确）。若 `_sessionDataCache` 是 private 字段，直接访问即可（同文件）。

- [ ] **Step 5: 运行确认通过**

Run: 同 Step 3
Expected: PASS

- [ ] **Step 6: 提交**

```bash
git add src/Seeing.Session/Core/ISessionManager.cs src/Seeing.Session/Management/SessionManager.cs tests/Seeing.Session.Tests/
git commit -m "feat(session): SessionManager.LoadChildrenFromStorageAsync 磁盘枚举子会话"
```

---

### Task 4: IStreamConsumer + CircuitContext + SessionEventStreamRouter

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/IStreamConsumer.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/CircuitContext.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/SessionEventStreamRouterTests.cs`

- [ ] **Step 1: 创建 IStreamConsumer 接口**

`samples/Seeing.Agent.WebUI/Services/IStreamConsumer.cs`：

```csharp
using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话事件流消费者策略。Router 按显式 sessionId 参数注册/分发，不读取 SessionId 属性；
/// 同时监听多会话的实现（如 TaskCardAggregator）返回其主会话 Id 作标识。
/// </summary>
public interface IStreamConsumer
{
    string SessionId { get; }

    void OnEvent(IMessageEvent evt);

    void OnStreamEnd();
}
```

- [ ] **Step 2: 创建 CircuitContext**

`samples/Seeing.Agent.WebUI/Services/CircuitContext.cs`：

```csharp
namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 当前 circuit 的 Id 载体（Scoped）。SeeingCircuitHandler 在 OnCircuitOpenedAsync 写入，
/// 页面在自身 scope 内读取后传给 Router 工厂，使 Singleton Router 能按 circuit 关联 consumer。
/// </summary>
public sealed class CircuitContext
{
    public string? Id { get; set; }
}
```

- [ ] **Step 3: 编写 Router 测试（红）**

创建 `tests/Seeing.Agent.WebUI.Tests/Services/SessionEventStreamRouterTests.cs`。先确认测试项目命名空间与现有测试模式（读 `tests/Seeing.Agent.WebUI.Tests` 目录一个现有文件）。用 Moq 模拟 `IChatOrchestrator.SubscribeEvents` 返回受控事件流：

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionEventStreamRouterTests
{
    private sealed class FakeConsumer : IStreamConsumer
    {
        public string SessionId { get; }
        public List<IMessageEvent> Events { get; } = new();
        public bool StreamEnded { get; private set; }
        public FakeConsumer(string sessionId) => SessionId = sessionId;
        public void OnEvent(IMessageEvent evt) => Events.Add(evt);
        public void OnStreamEnd() => StreamEnded = true;
    }

    [Fact]
    public async Task AttachConsumer_ShouldStartLoopAndBroadcast()
    {
        var evt = new LoopStartEvent { SessionId = "s1", LoopId = "l1" };
        var channel = Channel.CreateUnbounded<IMessageEvent>();
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
            .Returns(channel.Reader.ReadAllAsync());

        await using var router = new SessionEventStreamRouter(
            orchestrator.Object, Mock.Of<IServiceScopeFactory>());
        var consumer = new FakeConsumer("s1");
        router.AttachConsumer("s1", consumer);

        await channel.Writer.WriteAsync(evt);
        await Task.Delay(100);
        consumer.Events.Should().ContainSingle(e => ReferenceEquals(e, evt));

        router.DetachConsumer("s1", consumer);
    }
}
```

需在文件顶部 `using System.Threading.Channels;`。若 `SessionEventStreamRouter` 依赖的 `IServiceScopeFactory` 参数不易 mock，调整构造签名（见 Step 4 实现）。此测试验证：Attach 启动 consume loop、事件广播给消费者、Detach 停止。

- [ ] **Step 4: 运行确认失败**

Run: `dotnet build tests/Seeing.Agent.WebUI.Tests -c Debug && dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionEventStreamRouterTests"`
Expected: FAIL（编译失败：类型不存在）

- [ ] **Step 5: 实现 Router**

`samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`：

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 统一多流订阅路由器（Singleton）：每个会话的事件流只订阅一次，广播给多个消费者。
/// 支持：引用快照去重（skipSet 按 loop 创建时快照构建，历史事件不广播）、
/// replay 补历史（replay=true 消费者 Attach 时对 buffer 全量 OnEvent 一次）、
/// circuit scope 管理（consumer→circuit 反向映射，Detach/circuit 关闭释放 scope）。
/// </summary>
public sealed class SessionEventStreamRouter : IDisposable
{
    private readonly IChatOrchestrator _orchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionEventStreamRouter> _logger;
    private readonly ConcurrentDictionary<string, SessionSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<IStreamConsumer, string> _consumerCircuit = new();
    private readonly ConcurrentDictionary<(string circuit, string session), IServiceScope> _scopes = new();
    private bool _disposed;

    public SessionEventStreamRouter(
        IChatOrchestrator orchestrator,
        IServiceScopeFactory scopeFactory,
        ILogger<SessionEventStreamRouter> logger)
    {
        _orchestrator = orchestrator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void AttachConsumer(string sessionId, IStreamConsumer consumer, bool replay = false)
    {
        if (string.IsNullOrEmpty(sessionId) || consumer == null)
            return;

        var sub = _subscriptions.GetOrAdd(sessionId, _ => new SessionSubscription());
        sub.Consumers.Add(consumer);

        // replay：对 buffer 全量事件补一次历史（随后收实时）
        if (replay)
        {
            foreach (var evt in _orchestrator.GetBufferedEvents(sessionId))
                SafeInvoke(consumer, evt);
        }

        if (sub.Loop == null)
        {
            var skipSet = new HashSet<IMessageEvent>(
                _orchestrator.GetBufferedEvents(sessionId), ReferenceEqualityComparer.Instance);
            var cts = new CancellationTokenSource();
            sub.Loop = ConsumeLoopAsync(sessionId, cts.Token, skipSet);
            sub.Cts = cts;
        }
    }

    public T GetOrCreateConsumer<T>(string sessionId, string circuitId)
        where T : IStreamConsumer
    {
        var scope = _scopeFactory.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<T>();
        _consumerCircuit[consumer] = circuitId;
        _scopes[(circuitId, sessionId)] = scope;
        return consumer;
    }

    public void DetachConsumer(string sessionId, IStreamConsumer consumer)
    {
        if (!_subscriptions.TryGetValue(sessionId, out var sub))
            return;

        sub.Consumers.TryRemove(consumer);
        if (_consumerCircuit.TryRemove(consumer, out var circuit))
        {
            if (_scopes.TryRemove((circuit, sessionId), out var scope))
                scope.Dispose();
        }

        if (sub.Consumers.IsEmpty)
            ReleaseSubscription(sessionId, sub);
    }

    public void DetachAllForCircuit(string circuitId)
    {
        foreach (var (key, scope) in _scopes.Where(kv => kv.Key.circuit == circuitId).ToList())
        {
            _scopes.TryRemove(key, out _);
            scope.Dispose();
        }

        foreach (var (consumer, cid) in _consumerCircuit.Where(kv => kv.Value == circuitId).ToList())
        {
            _consumerCircuit.TryRemove(consumer, out _);
            // 从所有订阅中移除该消费者
            foreach (var sub in _subscriptions.Values)
            {
                sub.Consumers.TryRemove(consumer);
                if (sub.Consumers.IsEmpty)
                    ReleaseSubscription(sub);
            }
        }
    }

    private async Task ConsumeLoopAsync(string sessionId, CancellationToken ct, HashSet<IMessageEvent> skipSet)
    {
        try
        {
            await foreach (var evt in _orchestrator.SubscribeEvents(sessionId, ct))
            {
                if (skipSet.Contains(evt))
                    continue;
                Broadcast(sessionId, evt);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话事件流消费循环异常终止: {SessionId}", sessionId);
        }
        finally
        {
            if (_subscriptions.TryGetValue(sessionId, out var sub) && sub.Loop != null)
            {
                foreach (var consumer in sub.Consumers.ToList())
                    SafeInvoke(consumer, null); // OnStreamEnd
                ReleaseSubscription(sessionId, sub);
            }
        }
    }

    private void Broadcast(string sessionId, IMessageEvent evt)
    {
        if (!_subscriptions.TryGetValue(sessionId, out var sub))
            return;
        foreach (var consumer in sub.Consumers.ToList())
            SafeInvoke(consumer, evt);
    }

    private static void SafeInvoke(IStreamConsumer consumer, IMessageEvent? evt)
    {
        try
        {
            if (evt == null) consumer.OnStreamEnd();
            else consumer.OnEvent(evt);
        }
        catch (Exception ex)
        {
            // 单消费者异常隔离，不终止 consume loop
        }
    }

    private void ReleaseSubscription(string sessionId, SessionSubscription sub) =>
        _subscriptions.TryRemove(sessionId, out _);

    private void ReleaseSubscription(SessionSubscription sub)
    {
        sub.Cts?.Cancel();
        sub.Cts?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var scope in _scopes.Values)
            scope.Dispose();
        _scopes.Clear();
        foreach (var sub in _subscriptions.Values)
            ReleaseSubscription(sub);
        _subscriptions.Clear();
    }

    private sealed class SessionSubscription
    {
        public ConcurrentBag<IStreamConsumer> Consumers { get; } = new();
        public Task? Loop { get; set; }
        public CancellationTokenSource? Cts { get; set; }
    }
}
```

**注意**：上述代码中 `ReleaseSubscription` 有两个重载且逻辑需保持一致（一个只移除订阅、一个还取消 CTS）——实现时收敛为：`ReleaseSubscription` 统一"取消 CTS + 移除订阅"。`ConsumeLoopAsync` 的 `finally` 里调用需避免并发双重移除，用 `TryRemove` 幂等。请按此意图修正实现后进入测试。若 `ReferenceEqualityComparer.Instance` 不可用（net10 有），用 `System.Collections.Generic.ReferenceEqualityComparer` 的命名空间确认。

- [ ] **Step 6: 运行确认通过**

Run: 同 Step 4
Expected: PASS

- [ ] **Step 7: 补充测试——replay 补历史 + 异常隔离**

在测试文件追加：

```csharp
    [Fact]
    public async Task AttachConsumer_WithReplay_ShouldDeliverBufferedEventsOnce()
    {
        var buffered = new List<IMessageEvent>
        {
            new ToolCallEvent { SessionId = "s1", Type = MessageEventType.ToolCallRunning,
                ToolCallId = "t1", ToolName = "read", Status = ToolCallStatus.Running }
        };
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.GetBufferedEvents("s1")).Returns(buffered);
        var channel = Channel.CreateUnbounded<IMessageEvent>();
        orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
            .Returns(channel.Reader.ReadAllAsync());

        await using var router = new SessionEventStreamRouter(
            orchestrator.Object, Mock.Of<IServiceScopeFactory>(), NullLogger.Instance);
        var consumer = new FakeConsumer("s1");
        router.AttachConsumer("s1", consumer, replay: true);

        await Task.Delay(100);
        consumer.Events.Should().ContainSingle(); // buffer 补历史一次
    }
```

若 `NullLogger.Instance` 类型不符（应为 `Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance` 或 `Mock.Of<ILogger<...>>`），用 `Mock.Of<Microsoft.Extensions.Logging.ILogger<SessionEventStreamRouter>>()`。

- [ ] **Step 8: 运行测试**

Run: 同 Step 4
Expected: 全部 PASS

- [ ] **Step 9: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/IStreamConsumer.cs samples/Seeing.Agent.WebUI/Services/CircuitContext.cs samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs tests/Seeing.Agent.WebUI.Tests/
git commit -m "feat(webui): 统一多流订阅基础设施 SessionEventStreamRouter"
```

---

### Task 5: EventStreamHandler 多实例化重构

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/EventStreamHandlerTests.cs`

- [ ] **Step 1: 调研现有依赖面**

先完整阅读 `EventStreamHandler.cs`（970 行）并 grep `Session.razor`/`BlazorPermissionChannel` 中对 `EventStreamHandler` 的所有调用点，列出每个方法的使用方。构造从 `(SessionState, ISessionManager)` 改为 `(string sessionId, ISessionManager)`。**关键改造**：
- 删除 `_sessionState` 字段；新增 `public string SessionId { get; }`。
- 所有 `_sessionState.CurrentSession` → `_sessionManager.Get(SessionId)`。
- `OnStateChanged` 从 `Action?` 改为 `Action<IMessageEvent>?`。
- 新增 `CurrentTodoList`/`BudgetStatus` 只读查询属性（迁移自 SessionState 相关字段的读取）。
- 新增 `IsStreamActive`/`ExecutionStatus` 属性 + 内部 `_activeExecutionId` 跟踪；`ExecutionCompleteEvent` 匹配 `_activeExecutionId` 才触发执行态清理。
- 移除 `UpdateTodoList`/`UpdateBudgetStatus` 对 `_sessionState` 的写入，改存自身字段并上抛 `OnStateChanged(evt)`。
- `ClearCache()` 保留（供页面重访会话时清理流式状态）。

- [ ] **Step 2: 编写测试（红）**

创建 `tests/Seeing.Agent.WebUI.Tests/Services/EventStreamHandlerTests.cs`（先读现有测试确认 fixture 模式与 `SessionState`/`ISessionManager` 的 mock 方式）。核心断言：

```csharp
    [Fact]
    public async Task ProcessEventAsync_ToolCallRunning_ShouldWriteToolCallToOwnSession()
    {
        var session = SessionData.Create("p1", "general");
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.Get("s1")).Returns(session);
        var handler = new EventStreamHandler("s1", manager.Object);

        await handler.ProcessEventAsync(new ToolCallEvent
        {
            SessionId = "s1", Type = MessageEventType.ToolCallRunning,
            ToolCallId = "t1", ToolName = "task", Status = ToolCallStatus.Running
        });

        var msg = session.GetActiveMessages().Last(m => m.Role == "assistant");
        msg.ToolCalls.Should().ContainSingle(t => t.Id == "t1");
    }
```

`SessionData.Create` 签名需确认（`Create(string? partitionId, string? selectedAgent)`）；`SessionMessage.AssistantMessage` 构造方式参照 `ChatEventTracker.EnsureAssistantMessage`。

- [ ] **Step 3: 运行确认失败**

Run: `dotnet build tests/Seeing.Agent.WebUI.Tests -c Debug && dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~EventStreamHandlerTests"`
Expected: FAIL

- [ ] **Step 4: 重构 EventStreamHandler**

按 Step 1 的改造清单实施。**保留所有对外行为**（HandleToolCall 的 task 分支暂不接聚合器，Task 6 处理）。逐处替换 `_sessionState.CurrentSession` 引用。**注意**：`EnsureLiveSessionSynced` 依赖 `_sessionState`，改为基于 `_sessionManager.Get(SessionId)` 判断引用一致性（若 `_sessionManager.Get(SessionId)` 返回不同实例则更新本地指针）。

- [ ] **Step 5: 运行确认通过**

Run: 同 Step 3
Expected: PASS

- [ ] **Step 6: 全量编译确认无残留调用点**

Run: `dotnet build samples/Seeing.Agent.WebUI -c Debug`
Expected: 成功（`Session.razor`/`BlazorPermissionChannel` 等调用点需同步适配：构造参数、`OnStateChanged` 签名、`GetCurrentAssistantMessage` 等）。**注意**：`Session.razor` 的适配属 Task 7，此处先保证编译通过的最小适配（保留页面仍能工作的临时路径，Task 7 最终接管）。

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs tests/Seeing.Agent.WebUI.Tests/
git commit -m "refactor(webui): EventStreamHandler 多实例化（绑定 sessionId，解耦 SessionState）"
```

---

### Task 6: TaskCardAggregator

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs`

- [ ] **Step 1: 实现 TaskCardAggregator**

`samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs`：

```csharp
using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Task 卡片聚合器（每父会话一实例）：作为父会话流 consumer 识别 task 工具调用，
/// 自动挂载子会话订阅，消费子会话事件聚合 SessionTaskStep 写回父 toolCall.TaskSteps。
/// 并发写契约：只写 TaskSteps/Error/TaskId，整体替换 TaskSteps 列表引用，不碰 Status/Result。
/// </summary>
public sealed class TaskCardAggregator : IStreamConsumer
{
    private readonly SessionEventStreamRouter _router;
    private readonly ISessionManager _sessionManager;
    private readonly IChatOrchestrator _orchestrator;

    private readonly ConcurrentDictionary<string, TaskCardState> _tasks = new();

    public TaskCardAggregator(
        SessionEventStreamRouter router,
        ISessionManager sessionManager,
        IChatOrchestrator orchestrator)
    {
        _router = router;
        _sessionManager = sessionManager;
        _orchestrator = orchestrator;
    }

    public string SessionId => ParentSessionId;

    public string? ParentSessionId { get; private set; }

    public event Action<SessionMessage>? AssistantChanged;

    /// <summary>会话切换重绑：清空 tasks、重设父会话、重订阅父流。</summary>
    public void Rebind(string parentSessionId)
    {
        foreach (var (taskId, state) in _tasks)
        {
            if (!state.Detached)
                _router.DetachConsumer(taskId, this);
        }
        _tasks.Clear();
        ParentSessionId = parentSessionId;
        _router.AttachConsumer(parentSessionId, this);
    }

    /// <summary>页面恢复：重扫会话内所有 task 工具调用。</summary>
    public void Reconcile(string parentSessionId)
    {
        ParentSessionId = parentSessionId;
        var session = _sessionManager.Get(parentSessionId);
        if (session?.Messages == null) return;
        foreach (var msg in session.Messages)
        {
            if (msg.ToolCalls == null) continue;
            foreach (var tc in msg.ToolCalls)
            {
                if (IsTaskToolCall(tc))
                    AttachFromToolCall(parentSessionId, tc, msg);
            }
        }
    }

    public void OnEvent(IMessageEvent evt)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        // 父流事件：识别 task 工具调用
        if (string.Equals(sessionId, ParentSessionId, StringComparison.Ordinal))
        {
            if (evt is ToolCallEvent tool && IsTaskToolName(tool.ToolName))
            {
                var toolCall = ResolveToolCall(tool);
                if (toolCall != null)
                    AttachFromToolCall(sessionId, toolCall, ResolveOwnerMessage(toolCall));
            }
            return;
        }

        // 子流事件：聚合
        if (_tasks.TryGetValue(sessionId, out var state) && !state.Detached)
        {
            if (ApplyChildEvent(state, evt))
            {
                SchedulePersist();
                AssistantChanged?.Invoke(state.Owner);
            }
            else
            {
                state.Detached = true;
                _router.DetachConsumer(sessionId, this);
            }
        }
    }

    public void OnStreamEnd()
    {
        // 会话流结束：标记相关 task 结束
    }

    private void AttachFromToolCall(string parentSessionId, SessionToolCall toolCall, SessionMessage owner)
    {
        var key = toolCall.Id;
        if (_tasks.TryGetValue(key, out var existing) && !existing.Detached)
            return; // 幂等

        var taskId = ResolveTaskId(parentSessionId, toolCall);
        if (string.IsNullOrEmpty(taskId))
        {
            ScheduleRetry(parentSessionId, toolCall, owner);
            return;
        }

        toolCall.TaskId = taskId;
        var state = new TaskCardState(taskId, toolCall, owner);
        _tasks[key] = state;
        _router.AttachConsumer(taskId, this, replay: true);
    }

    private string? ResolveTaskId(string parentSessionId, SessionToolCall toolCall)
    {
        if (!string.IsNullOrEmpty(toolCall.TaskId))
            return toolCall.TaskId;

        var children = _sessionManager.ListChildrenAsync(parentSessionId, SessionKind.SubAgent)
            .GetAwaiter().GetResult();
        var match = children.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        if (match != null) return match.Id;

        // 冷缓存兜底：磁盘枚举子会话后重查
        var diskChildren = _sessionManager.LoadChildrenFromStorageAsync(parentSessionId)
            .GetAwaiter().GetResult();
        match = diskChildren.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        return match?.Id;
    }

    private void ScheduleRetry(string parentSessionId, SessionToolCall toolCall, SessionMessage owner)
    {
        // 定时重试 10×500ms（前台路径父 Loop 阻塞，无事件驱动）
        Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                if (ParentSessionId != parentSessionId) return;
                if (!string.IsNullOrEmpty(toolCall.TaskId) || _tasks.ContainsKey(toolCall.Id)) return;
                var taskId = ResolveTaskId(parentSessionId, toolCall);
                if (string.IsNullOrEmpty(taskId)) continue;
                toolCall.TaskId = taskId;
                var state = new TaskCardState(taskId, toolCall, owner);
                _tasks[toolCall.Id] = state;
                _router.AttachConsumer(taskId, this, replay: true);
                return;
            }
        });
    }

    private static bool ApplyChildEvent(TaskCardState state, IMessageEvent evt)
    {
        switch (evt)
        {
            case ToolCallEvent tool:
                MergeStep(state.ToolCall, tool);
                return true;
            case ExecutionCompleteEvent:
                return false; // 终态：结束订阅（不改 Status，由父工具状态为准）
            case LoopCancelledEvent:
                if (IsIncomplete(state.ToolCall))
                    state.ToolCall.Error = "子任务已取消";
                return false; // 即 Detach
            case ErrorEvent err:
                if (IsIncomplete(state.ToolCall))
                    state.ToolCall.Error = err.Message;
                return false;
            default:
                return true;
        }
    }

    private static void MergeStep(SessionToolCall toolCall, ToolCallEvent tool)
    {
        var steps = toolCall.TaskSteps?.ToList() ?? new List<SessionTaskStep>();
        var stepKind = tool.Type switch
        {
            MessageEventType.ToolCallPending => "tool_pending",
            MessageEventType.ToolCallRunning => "tool_running",
            MessageEventType.ToolCallComplete => "tool_complete",
            _ => "tool_running"
        };
        var existing = steps.FirstOrDefault(s => s.ToolCallId == tool.ToolCallId);
        var merged = new SessionTaskStep
        {
            StepKind = stepKind,
            ToolCallId = tool.ToolCallId,
            ToolName = tool.ToolName,
            Status = tool.Status.ToString(),
            Preview = Truncate(tool.Output ?? tool.Error),
            Timestamp = tool.Timestamp
        };
        if (existing != null)
            steps[steps.IndexOf(existing)] = merged;
        else
            steps.Add(merged);

        // 整体替换列表引用（并发写契约）
        toolCall.TaskSteps = steps;
    }

    private void SchedulePersist()
    {
        // 防抖落盘（1s）+ 串行化链；终态由 OnEvent 返回 false 时立即落盘
        // 实现：Timer/任务链对 ParentSessionId 调 SaveAsync
        // （此方法在本任务中先实现防抖 Timer 触发 SaveAsync(ParentSessionId)）
    }

    private static bool IsIncomplete(SessionToolCall tc)
    {
        var status = tc.Status?.ToLowerInvariant();
        return status is null or "pending" or "running";
    }

    private SessionToolCall? ResolveToolCall(ToolCallEvent evt) =>
        _sessionManager.Get(evt.SessionId)?.Messages
            ?.SelectMany(m => m.ToolCalls ?? new List<SessionToolCall>())
            .FirstOrDefault(t => t.Id == evt.ToolCallId);

    private SessionMessage? ResolveOwnerMessage(SessionToolCall toolCall) =>
        _sessionManager.Get(ParentSessionId ?? string.Empty)?.Messages
            ?.FirstOrDefault(m => m.ToolCalls?.Contains(toolCall) == true);

    private static bool IsTaskToolCall(SessionToolCall tc) =>
        string.Equals(tc.Name, "task", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(tc.TaskId);

    private static bool IsTaskToolName(string? name) =>
        string.Equals(name, "task", StringComparison.OrdinalIgnoreCase);

    private static string? Truncate(string? text, int max = 200)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private sealed class TaskCardState
    {
        public string TaskId { get; }
        public SessionToolCall ToolCall { get; }
        public SessionMessage Owner { get; }
        public bool Detached { get; set; }
        public TaskCardState(string taskId, SessionToolCall toolCall, SessionMessage owner)
        { TaskId = taskId; ToolCall = toolCall; Owner = owner; }
    }
}
```

**注意**：`SchedulePersist` 的防抖 Timer 实现需在 Task 中补全（用 `System.Threading.Timer` + `SemaphoreSlim` 串行队列，对 `ParentSessionId` 调 `_sessionManager.SaveAsync`；Detach/`OnStreamEnd` 时 flush）。`IChatOrchestrator` 依赖可去除（Router 内部已用），如未用到则从构造移除。`ScheduleRetry` 里 `Task.Run` 的循环要处理 cancellation（用简单标记即可）。实现时按此意图收敛。

- [ ] **Step 2: 编写测试（红）**

创建 `tests/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs`。用 Mock `ISessionManager`（返回含 task 工具调用的 SessionData）+ 真实 `SessionEventStreamRouter`（Mock `IChatOrchestrator` 的事件流）+ Mock `IChatOrchestrator.GetBufferedEvents`。核心断言：

```csharp
    [Fact]
    public async Task OnEvent_ChildToolCall_ShouldAggregateTaskSteps()
    {
        // 构造：父会话含 task toolCall(TaskId="child1")；聚合器 Attach 子流后
        // 收到子会话 ToolCallEvent → toolCall.TaskSteps 增加一步
    }

    [Fact]
    public async Task OnEvent_ExecutionComplete_ShouldDetach()
    {
        // 收到子会话 ExecutionCompleteEvent → 订阅移除、不再更新
    }
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet build tests/Seeing.Agent.WebUI.Tests -c Debug && dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskCardAggregatorTests"`
Expected: FAIL

- [ ] **Step 4: 实现补全 + 测试通过**

按 Step 1 代码实现，补全 `SchedulePersist`（防抖落盘 + 串行化 + flush）后运行测试。
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs tests/Seeing.Agent.WebUI.Tests/
git commit -m "feat(webui): TaskCardAggregator 子会话事件聚合 TaskSteps"
```

---

### Task 7: Session.razor 接入 Router + 节流保留

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/SeeingCircuitHandler.cs`

- [ ] **Step 1: 移除 ConsumeEventStreamAsync 订阅循环**

`samples/Seeing.Agent.WebUI/Pages/Session.razor`：删除 `ConsumeEventStreamAsync`（约 1500-1695 行）中的 `await foreach` 订阅循环与事件分派逻辑；将事件处理改由 `OnHandlerChanged(IMessageEvent evt)` 回调承接（保留节流、压缩、导航、命令等页面 UI 反应，按 spec §8.2 事件分派表迁移）。

**改动要点**：
- 新增字段：`private EventStreamHandler? _handler; private TaskCardAggregator? _aggregator; private bool _handlerBound;`
- `OnParametersSetAsync` 加载会话后（`RestoreExecutionFromOverview` 处）：
```csharp
if (!_handlerBound)
{
    var circuitId = CircuitContext.Id ?? "";
    _handler = Router.GetOrCreateConsumer<EventStreamHandler>(SessionId, circuitId);
    _handler.ClearCache();
    _aggregator = Router.GetOrCreateConsumer<TaskCardAggregator>(SessionId, circuitId);
    _aggregator.Rebind(SessionId);
    _handler.OnStateChanged += OnHandlerChanged;
    _aggregator.AssistantChanged += OnAggregatorAssistantChanged;
    Router.AttachConsumer(SessionId, _handler);
    Router.AttachConsumer(SessionId, _aggregator);
    _handlerBound = true;
}
```
- `OnHandlerChanged(IMessageEvent evt)`：保留节流（仅 `StreamDelta` 走 charCount/代码块阈值），其余事件立即 `SyncTimelineAssistantFromStream`/`ReconcileTimelineFromSession`；按事件类型处理压缩/导航/命令（从旧循环迁移）。
- `OnAggregatorAssistantChanged(SessionMessage msg)` → `Timeline.SyncAssistantMessage(msg, SessionState.SessionId ?? "")`。
- 提交执行后不再手动起循环（Router 已订阅）。
- `Dispose`：`Router.DetachConsumer(旧会话Id, _handler/_aggregator)` + 退订事件 + `Router.DetachAllForCircuit(circuitId)`（或按需）。

- [ ] **Step 2: SeeingCircuitHandler 写 CircuitContext + 回调清理**

`samples/Seeing.Agent.WebUI/Services/SeeingCircuitHandler.cs`：注入 `CircuitContext`（Scoped）与 `SessionEventStreamRouter`（Singleton）：

```csharp
    private readonly CircuitContext _circuitContext;
    private readonly SessionEventStreamRouter _router;

    public SeeingCircuitHandler(
        ILogger<SeeingCircuitHandler> logger,
        CircuitTracker tracker,
        CircuitContext circuitContext,
        SessionEventStreamRouter router)
    {
        _logger = logger;
        _tracker = tracker;
        _circuitContext = circuitContext;
        _router = router;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _tracker.Register(circuit.Id);
        _circuitContext.Id = circuit.Id;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _tracker.Remove(circuit.Id);
        _router.DetachAllForCircuit(circuit.Id);
        return Task.CompletedTask;
    }
```

**注意**：`CircuitHandler` 是 Scoped（Program.cs:110），注入 Singleton Router 合法；注入 Scoped CircuitContext 同 scope 合法。需确认 CircuitHandler 每次 circuit 是否新建实例（Blazor Server 中 CircuitHandler 按 circuit 实例化，Scoped 语义成立）。

- [ ] **Step 3: 编译 + 运行 WebUI 测试**

Run: `dotnet build samples/Seeing.Agent.WebUI -c Debug && dotnet build tests/Seeing.Agent.WebUI.Tests -c Debug && dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
Expected: 编译成功，WebUI 测试全部 PASS

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/Session.razor samples/Seeing.Agent.WebUI/Services/SeeingCircuitHandler.cs
git commit -m "feat(webui): Session 页经 Router 订阅多流，circuit 关闭清理"
```

---

### Task 8: DI 注册 + 全量构建与测试

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Test: 全量回归

- [ ] **Step 1: Program.cs 注册**

`samples/Seeing.Agent.WebUI/Program.cs`，在 `EventStreamHandler` 注册附近添加：

```csharp
builder.Services.AddScoped<CircuitContext>();
builder.Services.AddSingleton<SessionEventStreamRouter>();
builder.Services.AddScoped<TaskCardAggregator>();
builder.Services.AddScoped<IStreamConsumer>(sp => sp.GetRequiredService<EventStreamHandler>()); // 可选：统一注册
```

`SessionEventStreamRouter` 依赖 `IChatOrchestrator`（现为 Singleton，Task 2 已改）+ `IServiceScopeFactory`（框架提供）+ `ILogger`——DI 自动解析。`EventStreamHandler` 保持 Scoped。

- [ ] **Step 2: 全量构建**

Run: `dotnet build Seeing.Agent.slnx -c Debug`
Expected: 成功

- [ ] **Step 3: 全量测试**

Run:
```bash
dotnet build tests/Seeing.Agent.Tests -c Debug
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet build tests/Seeing.Agent.WebUI.Tests -c Debug
dotnet vstest tests/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet test tests/Seeing.Session.Tests
```
Expected: 全部 PASS

- [ ] **Step 4: 手工冒烟（可选）**

Run: `dotnet run --project samples/Seeing.Agent.WebUI`
验证：父会话发消息触发 task 子代理，执行中卡片有子会话 ID + 进度步骤；刷新后进度保留；切换会话不串台。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Program.cs
git commit -m "feat(webui): DI 注册多流订阅基础设施与聚合器"
```

---

## 自审

**Spec 覆盖对照：**
- §4.1 TaskTool 元数据 → Task 1
- §4.2 ChatOrchestrator Singleton → Task 2
- §5.1/5.2 Router/IStreamConsumer/CircuitContext → Task 4
- §6 EventStreamHandler 多实例化 → Task 5
- §7 TaskCardAggregator → Task 6
- §8 页面接入/节流/circuit 清理 → Task 7
- DI 注册 → Task 8
- §10 测试 → 各任务内嵌
- LoadChildrenFromStorageAsync → Task 3

**占位符扫描**：`SchedulePersist`/`ScheduleRetry` 已给出意图但实现细节（Timer/串行化/flush）标注"按此意图收敛"——需在实现时补全，不属于含糊占位（已指明机制）。

**类型一致性**：`IStreamConsumer`（Task 4）在 Task 5/6/7 使用一致；`GetOrCreateConsumer<T>(sessionId, circuitId)` 签名在 Task 4 定义、Task 7 使用一致；`SessionMetadataKeys.OriginToolCallId` 在 Task 1 定义、Task 3/6 使用一致；`LoadChildrenFromStorageAsync(parentId, ct)` 在 Task 3 定义、Task 6 调用一致（Task 6 用无参形式 `LoadChildrenFromStorageAsync(parentSessionId)`，ct 有默认值，一致）。

**已知注意点**（实现时需现场核实）：EventStreamHandler 现有 `EnsureLiveSessionSynced`、`Session.razor` 中 `BlazorPermissionChannel` 对 handler 的依赖、`ReconcileOrphanTaskCardsAsync` 等——Task 5 已标注"最小适配保编译"，Task 7 最终接管。

---

## 最终实现状态（2026-08-27 合并后）

**状态：** 全部 Task 1-8 已实现并 fast-forward 合入 `master`（分支 `feat/task-card-ui-aggregation` → `b5f5c74`）。

**测试结果（最终）：**
- `Seeing.Agent.Tests` **766/766**
- `Seeing.Agent.WebUI.Tests` **95/95**（含最终审查修复新增的跨 circuit / 流重启 / 聚合器复用 / dispose flush 测试）
- `Seeing.Session.Tests` **100/100**
- 全解决方案编译 **0 错误**

**合并前最终整体审查修复（`19e5917`）：** C1 跨 circuit consumer 共享（键改 `(circuitId, sessionId, type)`）、I1 流重启（AttachConsumer 重建 loop + 页面提交前补挂载）、I2 聚合器按 circuit 复用（方案 A，跨父会话不累积）、I3 flush-before-dispose（聚合器 IDisposable）。另 M3 handler.RestoreExecution 恢复执行态。

**额外修复（`b5f5c74`，合并前发现）：** Task 卡片"结果摘要"被 `TaskMessageComponent.razor` 的 `@Truncate(ToolCall.Result, 400)` 硬截断 400 字符。根因：上游 `ToolCall.Result` 完整（`ChatEventTracker.cs:124`），唯一切断点在 UI。改为直接展示完整 `ToolCall.Result`（依赖 `is-collapsed` 类滚动）。

**实现路径说明：** 采用 Subagent-Driven 执行、关键步骤整体审查（非逐任务审查），最终整体审查签字后修复 Critical/Important 再合并。

**提交清单：**
```
5a437e4 feat(task): TaskTool 写子会话关联元数据 origin_tool_call_id
33cbec0 refactor(orchestrator): ChatOrchestrator 移除死字段并变 Singleton
1ca3474 feat(session): SessionManager.LoadChildrenFromStorageAsync 磁盘枚举子会话
4a00ace feat(webui): 统一多流订阅基础设施 SessionEventStreamRouter
6f32d35 refactor(webui): EventStreamHandler 多实例化（绑定 sessionId，解耦 SessionState）
95a48e7 feat(webui): TaskCardAggregator 子会话事件聚合 TaskSteps
3159e27 fix(webui): 修复多流聚合并发与 Router scope 泄漏，补 DI 注册
e66f24f fix(tests): FakeSessionManager 补 LoadChildrenFromStorageAsync 实现
d1bebee feat(webui): Session 页经 Router 订阅多流，移除自写事件循环
aec881a fix(webui): 页面刷新/切换后 Reconcile 补聚合 TaskSteps
97739e9 docs(webui): 明确 EventStreamHandler 占位注册为权限事件总线（不可移除）
19e5917 fix(webui): 修复跨 circuit consumer 共享/流重启/聚合器泄漏/circuit 关闭 flush
b5f5c74 fix(webui): TaskCard 结果摘要移除 400 字符硬截断，完整展示子会话结果
```

**后续可选增强（已知边界，未本期修）：** 刷新瞬时流尾弱化、排队闪断、Dispose 级联取消语义（后台任务应继续）、SafeInvoke 异常日志、LoadChildrenFromStorageAsync 全表扫缓存。
