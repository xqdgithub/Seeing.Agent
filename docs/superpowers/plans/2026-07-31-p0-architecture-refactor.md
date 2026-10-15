# Seeing.Agent P0 架构重构实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 Seeing.Agent 架构审查发现的 5 个 P0 级问题：DI 构造期同步阻塞、Agent 四接口职责重叠、McpClientManager 上帝类、HookManager 异常吞噬、装饰器链固化。

**Architecture:**
1. 用 `Lazy<T>` 延迟初始化 + `IHostedService` 异步加载替代 `.GetAwaiter().GetResult()`。
2. 将 `AgentManager` 拆分为 Store/ConfigLoader/RuntimeManager/Registry 四层职责。
3. 删除 `McpClientManager` 中的死代码（`IMcpClientWrapper` + 2 个 Wrapper 类）。
4. `HookManager` 引入有界 Channel 队列 + 异常事件 + 优雅关闭。
5. 装饰器链改为动态构建 + `ToolPolicies` 配置节 + `ToolsPage` UI 集成（档次 2）。

**Tech Stack:** .NET 10.0, C# 12+, `System.Threading.Channels`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, xUnit + FluentAssertions + Moq, Blazor Server + AntDesign Blazor。

---

## 全局约束

**⚠️ 重要：本计划全程不提交任何 git commit。所有"提交"步骤改为"暂存进度并等待人工确认"。**

每个 Task 的最后一步统一：

```
Run:
  dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug
  dotnet test  tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug
Expected: 编译无错误 + 所有相关测试通过。
完成后停下，等待人工 Review。**绝对不要执行 git add / git commit / git push。**
```

**依赖顺序（严格）**：

```
Task Group 1: P0-3 删除死代码       (无依赖，先做热身)
Task Group 2: P0-1 DI 异步初始化    (无依赖)
Task Group 3: P0-4 HookManager      (无依赖)
Task Group 4: P0-5 装饰器链 + UI    (弱依赖 P0-1 的 Bootstrapper)
Task Group 5: P0-2 Agent 四层拆分   (依赖 P0-1，最后做)
```

**已确认的 P0-5 附加设计决策**：
- ✅ 配置双层级（用户级 + 项目级）
- ✅ 立即生效（每次 `ExecuteAsync` 时 Resolve）
- ✅ 保留默认值兜底（30s / 3 次 / 5min）
- ✅ 先完全匹配（不做通配符匹配，作为后续扩展）

---

# Task Group 1: P0-3 - 删除 MCP 死代码（方案 B）

**目标：** 从 `McpClientManager.cs` 中删除 `IMcpClientWrapper` 接口和两个未使用的 Wrapper 类 (`StdioMcpClientWrapper` / `HttpMcpClientWrapper`)，共约 302 行死代码。

## Task 1.1: 验证死代码确实无引用

**Files:**
- Verify: `src/**/*.cs` + `tests/**/*.cs` + `samples/**/*.cs`

- [ ] **Step 1: 全项目搜索 IMcpClientWrapper 引用**

Run:
```powershell
Get-ChildItem -Path "src","tests","samples" -Recurse -Filter "*.cs" | Select-String "IMcpClientWrapper" | Select-Object Path,LineNumber,Line
```
Expected: 只有 `src/Seeing.Agent/MCP/McpClientManager.cs` 内部出现（接口定义 + 两个类实现），无任何外部引用。如果发现外部引用，**中止本 Task Group** 并汇报。

- [ ] **Step 2: 搜索 StdioMcpClientWrapper 引用**

Run:
```powershell
Get-ChildItem -Path "src","tests","samples" -Recurse -Filter "*.cs" | Select-String "StdioMcpClientWrapper" | Select-Object Path,LineNumber,Line
```
Expected: 只有 `McpClientManager.cs` 内部两处引用（类定义 + `HttpMcpClientWrapper.CallToolAsync` 里的 `StdioMcpClientWrapper.BuildToolResult` 静态方法调用）。

- [ ] **Step 3: 搜索 HttpMcpClientWrapper 引用**

Run:
```powershell
Get-ChildItem -Path "src","tests","samples" -Recurse -Filter "*.cs" | Select-String "HttpMcpClientWrapper" | Select-Object Path,LineNumber,Line
```
Expected: 只有 `McpClientManager.cs` 内部类定义处出现。

- [ ] **Step 4: 确认新版路径不依赖 BuildToolResult**

Run:
```powershell
Get-ChildItem -Path "src" -Recurse -Filter "*.cs" | Select-String "BuildToolResult" | Select-Object Path,LineNumber,Line
```
Expected: 仅 `McpClientManager.cs` 旧代码内部出现。新版 `ExecuteMcpToolAsync`（`McpClientManager.cs:835-892`）直接调用 `client.CallToolAsync(...)` 返回 `McpToolResult`，不再需要旧版转换。

## Task 1.2: 删除 #region 旧版包装器接口和类型 块

**Files:**
- Modify: `src/Seeing.Agent/MCP/McpClientManager.cs:945-1246`（删除 302 行）

- [ ] **Step 1: 读取待删除区块的准确范围**

使用 Read 工具读取 `McpClientManager.cs` 的 `945-1246` 行。确认：
- 起点：`    #region 旧版包装器接口和类型（兼容性保留）`
- 终点：`    #endregion`
- 结构：接口 `IMcpClientWrapper` + `StdioMcpClientWrapper` 类 + `HttpMcpClientWrapper` 类

- [ ] **Step 2: 使用 Edit 工具删除整块**

调用 Edit 工具：
- `filePath`: `src/Seeing.Agent/MCP/McpClientManager.cs`
- `oldString`: 从 `#region 旧版包装器接口和类型（兼容性保留）` 起点到 `#endregion` 终点的完整内容（含前后 1 个空行以保持缩进正确）
- `newString`: 空字符串

删除后应保留末尾类闭合 `}` 与 namespace 闭合 `}`。

- [ ] **Step 3: 清理无用 using**

Run:
```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug
```
若出现 `warning CS8019: 不必要的 using 指令`，用 Edit 工具删除对应 using（通常是 `using ModelContextProtocol.Client;` 中 `StdioClientTransport` / `HttpClientTransport` 相关，若主类 `McpClientManager` 仍在使用 `McpClient` 则保留）。

- [ ] **Step 4: 编译验证**

Run:
```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug
```
Expected: 编译成功、无警告新增。

- [ ] **Step 5: 运行 MCP 相关测试**

Run:
```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug --filter "FullyQualifiedName~MCP"
```
Expected: 4 个测试类全部通过：
- `McpClientManagerDisableTests`
- `McpOAuthTests`
- `McpServerStatusTests`
- `McpStateTransitionsDisconnectTests`

- [ ] **Step 6: 全量测试冒烟**

Run:
```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug
```
Expected: 无新增测试失败。

- [ ] **Step 7: 统计代码行数变化**

Run:
```powershell
(Get-Content src/Seeing.Agent/MCP/McpClientManager.cs).Length
```
Expected: 从 1247 行降到约 945 行（减少约 302 行）。

- [ ] **Step 8: 暂停等待人工确认**

**不提交。** 汇报：
- 已删除的行数
- 编译警告清单
- 测试通过率
- 等待人工 Review。

---


# Task Group 2: P0-1 - DI 构造期异步初始化改造

**目标：** 移除所有 DI 工厂内的 `.GetAwaiter().GetResult()`，改用 `Lazy<Task<T>>` + `IHostedService` 显式异步初始化。

**关键位置清单**：

| 文件 | 行号 | 问题 |
|------|------|------|
| `ServiceCollectionExtensions.cs` | 69,267 | `UnifiedConfigManager.LoadAsync().GetAwaiter().GetResult()` |
| `ServiceCollectionExtensions.cs` | 343 | `discovery.DiscoverAgentsAsync().GetAwaiter().GetResult()` |
| `AgentManager.cs` | 69,74,80 | 构造函数内 `Task.Run(...).GetAwaiter().GetResult()` |
| `ToolManager.cs` | 94,122,338,352 | 同步方法内部 `Task.Run(...).GetAwaiter().GetResult()` |
| `McpClientManager.cs` | 173,182,190,201 | Pause/Resume 同步方法 |

## Task 2.1: 定义异步初始化契约

**Files:**
- Create: `src/Seeing.Agent/Extensions/Bootstrap/IAsyncInitializable.cs`
- Create: `src/Seeing.Agent/Extensions/Bootstrap/SeeingAgentBootstrapper.cs`

- [ ] **Step 1: 创建 `IAsyncInitializable` 接口**

Create file `src/Seeing.Agent/Extensions/Bootstrap/IAsyncInitializable.cs`:

```csharp
namespace Seeing.Agent.Extensions.Bootstrap;

/// <summary>
/// 需要在应用启动阶段异步初始化的服务契约。
/// 由 <see cref="SeeingAgentBootstrapper"/> 在 IHostedService.StartAsync 中依次调用。
/// </summary>
public interface IAsyncInitializable
{
    /// <summary>初始化优先级（数值越小越先执行）。</summary>
    int InitializationOrder { get; }

    /// <summary>异步初始化。抛出异常将阻止宿主启动。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: 创建 `SeeingAgentBootstrapper` (IHostedService)**

Create file `src/Seeing.Agent/Extensions/Bootstrap/SeeingAgentBootstrapper.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Extensions.Bootstrap;

public sealed class SeeingAgentBootstrapper : IHostedService
{
    private readonly IEnumerable<IAsyncInitializable> _initializables;
    private readonly ILogger<SeeingAgentBootstrapper> _logger;

    public SeeingAgentBootstrapper(
        IEnumerable<IAsyncInitializable> initializables,
        ILogger<SeeingAgentBootstrapper> logger)
    {
        _initializables = initializables;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var ordered = _initializables.OrderBy(x => x.InitializationOrder).ToList();
        _logger.LogInformation("Seeing.Agent Bootstrapper 开始初始化 {Count} 个服务", ordered.Count);

        foreach (var svc in ordered)
        {
            var name = svc.GetType().Name;
            _logger.LogDebug("初始化 {Name} (order={Order})", name, svc.InitializationOrder);
            await svc.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("初始化 {Name} 完成", name);
        }
        _logger.LogInformation("Seeing.Agent Bootstrapper 初始化完成");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`
Expected: 编译成功。

## Task 2.2: 改造 UnifiedConfigManager 支持延迟加载

**Files:**
- Modify: `src/Seeing.Agent/Configuration/UnifiedConfigManager.cs`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:64-72, 108-116, 262-278`

- [ ] **Step 1: UnifiedConfigManager 实现 IAsyncInitializable**

在 `UnifiedConfigManager.cs` 文件顶部追加 using:
```csharp
using Seeing.Agent.Extensions.Bootstrap;
```

修改类声明:
```csharp
public sealed class UnifiedConfigManager : IConfigSectionStore, IAsyncInitializable
```

在类内追加（放到 `LoadAsync` 方法附近）:
```csharp
/// <inheritdoc />
public int InitializationOrder => 100;

/// <inheritdoc />
public Task InitializeAsync(CancellationToken cancellationToken = default)
    => LoadAsync(cancellationToken);
```

- [ ] **Step 2: 修改 DI 注册，移除所有 .GetAwaiter().GetResult()**

在 `ServiceCollectionExtensions.cs` 中找到三处 `manager.LoadAsync().GetAwaiter().GetResult();` （约 69、116、267 行），全部删除。

并将每处的注册改为:
```csharp
services.AddSingleton<UnifiedConfigManager>(sp =>
{
    var workspace = sp.GetRequiredService<IWorkspaceProvider>();
    var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
    return new UnifiedConfigManager(workspace, logger);
    // 加载改由 SeeingAgentBootstrapper 在 StartAsync 中按顺序调用
});
services.AddSingleton<IAsyncInitializable>(sp =>
    sp.GetRequiredService<UnifiedConfigManager>());
```

对自定义配置路径（约 108-116 行），保留 `configure()` 调用:
```csharp
services.AddSingleton<UnifiedConfigManager>(sp =>
{
    var workspace = sp.GetRequiredService<IWorkspaceProvider>();
    var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
    var manager = new UnifiedConfigManager(workspace, logger);
    configure(manager.GetSeeingAgentOptions());
    return manager;
});
```

- [ ] **Step 3: 注册 Bootstrapper 为 IHostedService**

在 `ServiceCollectionExtensions.RegisterCoreServices` 方法起始（约 305 行前）追加:
```csharp
// Seeing.Agent 启动引导器
services.AddHostedService<SeeingAgentBootstrapper>();
```

在文件顶部 using 区域追加:
```csharp
using Seeing.Agent.Extensions.Bootstrap;
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 2.3: 改造 AgentManager 构造期异步加载

**Files:**
- Modify: `src/Seeing.Agent/Configuration/AgentManager.cs:40-96`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:330-362`

- [ ] **Step 1: 移除构造函数内的同步注册**

打开 `AgentManager.cs` 构造函数（40-81 行），将:
```csharp
// 注册内置代理
if (builtInAgents != null)
{
    foreach (var agent in builtInAgents)
        Task.Run(async () => await _agentStore.RegisterAsync(agent)).GetAwaiter().GetResult();
}
// 备份原始 Agent 定义
foreach (var agent in Task.Run(async () => await _agentStore.GetAllAsync()).GetAwaiter().GetResult())
    _originalAgents[agent.Name] = agent;

_logger.LogInformation("AgentManager 初始化完成，已注册 {Count} 个代理",
    Task.Run(async () => await _agentStore.GetAllAsync()).GetAwaiter().GetResult().Count);
```

替换为:
```csharp
_builtInAgents = builtInAgents?.ToList() ?? new List<AgentDefinition>();
_discovery = null!; // 在 DI 注册后设置
_logger.LogInformation("AgentManager 已构造，待 StartAsync 完成初始化");
```

在类内追加字段:
```csharp
private readonly List<AgentDefinition> _builtInAgents;
private AgentDiscovery _discovery;
```

- [ ] **Step 2: 修改构造函数签名**

在构造函数参数列表末尾追加 `AgentDiscovery? discovery = null`:
```csharp
public AgentManager(
    ILogger<AgentManager> logger,
    IAgentStore agentStore,
    IAgentRuntimeManager runtimeManager,
    IWorkspaceProvider workspaceProvider,
    IEnumerable<AgentDefinition>? builtInAgents = null,
    string? defaultAgent = null,
    IOptions<SeeingAgentOptions>? options = null,
    AgentDiscovery? discovery = null)   // 新增
{
    _logger = logger;
    _agentStore = agentStore;
    _runtimeManager = runtimeManager;
    _workspaceProvider = workspaceProvider;
    _options = options;
    _discovery = discovery;
    _builtInAgents = builtInAgents?.ToList() ?? new List<AgentDefinition>();
    // ...(yaml serializer 初始化保留)
}
```

- [ ] **Step 3: 修改 StartAsync 完成全部初始化**

替换 `StartAsync`（约 83-90 行）:
```csharp
public async Task StartAsync(CancellationToken cancellationToken = default)
{
    _logger.LogInformation("AgentManager 正在初始化...");

    // 1. 注册内置 Agent
    foreach (var agent in _builtInAgents)
        await _agentStore.RegisterAsync(agent).ConfigureAwait(false);

    // 2. 从文件系统发现 Agent
    if (_discovery != null)
    {
        var discovered = await _discovery.DiscoverAgentsAsync().ConfigureAwait(false);
        foreach (var agent in discovered)
            await _agentStore.RegisterAsync(agent).ConfigureAwait(false);
    }

    // 3. 备份原始 Agent 定义
    var all = await _agentStore.GetAllAsync().ConfigureAwait(false);
    foreach (var agent in all)
        _originalAgents[agent.Name] = agent;

    // 4. 运行时初始化 + MD 覆盖
    await _runtimeManager.InitializeAsync().ConfigureAwait(false);
    await ApplyMdOverridesAsync().ConfigureAwait(false);

    _logger.LogInformation("AgentManager 初始化完成，已注册 {Count} 个代理", all.Count);
}
```

- [ ] **Step 4: 修改 DI 工厂**

在 `ServiceCollectionExtensions.cs`（约 330-356 行）找到 AgentManager 注册，将:
```csharp
var discoveredAgents = discovery.DiscoverAgentsAsync().GetAwaiter().GetResult();
var allAgents = builtInAgents.Concat(discoveredAgents);
var manager = new AgentManager(logger, agentStore, runtimeManager, workspaceProvider, allAgents, ...);
```

改为:
```csharp
var manager = new AgentManager(
    logger, agentStore, runtimeManager, workspaceProvider,
    builtInAgents: builtInAgents,
    defaultAgent: options?.Value?.DefaultAgent,
    options: options,
    discovery: discovery);
```

- [ ] **Step 5: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 2.4: 改造 ToolManager 同步 API

**Files:**
- Modify: `src/Seeing.Agent/Tools/ToolManager.cs:344-353, 92-103, 120-123, 338-339`

- [ ] **Step 1: 修改 RegisterTool 同步方法**

将 `RegisterTool` 同步方法（约 344-353 行）从:
```csharp
public void RegisterTool(ITool tool)
{
    Task.Run(async () => await RegisterToolAsync(tool)).GetAwaiter().GetResult();
}
```

改为（跳过异步 Hook，保留核心注册）:
```csharp
public void RegisterTool(ITool tool)
{
    if (tool == null || string.IsNullOrEmpty(tool.Id))
    {
        _logger.LogWarning("尝试注册无效工具，已跳过");
        return;
    }

    var finalTool = _decoratorRegistry?.Apply(tool) ?? tool;
    _tools[tool.Id] = finalTool;

    _hookManager.TriggerFireAndForget(
        HookRegistry.ToolAfterRegister, string.Empty,
        new Dictionary<string, object?> { ["toolId"] = tool.Id, ["tool"] = finalTool });
}
```

- [ ] **Step 2: 搜索并修改同步版本的调用方**

Run:
```powershell
Get-ChildItem -Path "src","tests","samples" -Recurse -Filter "*.cs" | Select-String "\.GetToolSchemasForMode\(|\.GetToolSchemas\(\)|\.GetToolSchemasForAgent\(" | Select-Object Path,LineNumber,Line
```

对每个调用点：若调用方是异步的，改为 `await ...Async()`；若是同步的，改为纯同步无 Hook 实现（直接从 `_tools.Values` 构建 schema）。

- [ ] **Step 3: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 2.5: 改造 McpClientManager Pause/Resume 为异步

**Files:**
- Modify: `src/Seeing.Agent/MCP/McpClientManager.cs:167-205`
- Modify: `src/Seeing.Agent/MCP/Core/IMcpController.cs`（接口签名）

- [ ] **Step 1: 将 4 个方法改为 Task 返回值 + Async 后缀**

将 `PauseServer(string name)` → `PauseServerAsync(string name, CancellationToken ct = default)`, 返回 `Task<McpOperationResult>`。
同理 `ResumeServer`, `PauseAllServers`, `ResumeAllServers`。

内部改为直接 `await coordinator.PauseAsync(ct)`。

- [ ] **Step 2: 搜索所有调用方并改为异步**

Run:
```powershell
Get-ChildItem -Path "src","tests" -Recurse -Filter "*.cs" | Select-String "\.PauseServer\(|\.ResumeServer\(|\.PauseAllServers\(|\.ResumeAllServers\(" | Select-Object Path,LineNumber,Line
```

对每个调用点改为 `await ...Async()`。

- [ ] **Step 3: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 2.6: 全量测试与残留检查

- [ ] **Step 1: 编译整个解决方案**

Run: `dotnet build Seeing.Agent.slnx -c Debug`

- [ ] **Step 2: 全量测试**

Run: `dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug`

- [ ] **Step 3: 检查 .GetAwaiter().GetResult() 残留**

Run:
```powershell
Get-ChildItem -Path "src/Seeing.Agent" -Recurse -Filter "*.cs" | Select-String "\.GetAwaiter\(\)\.GetResult\(\)" | Select-Object Path,LineNumber,Line
```
Expected: **零命中**（或仅保留明确注释的降级路径）。

- [ ] **Step 4: 暂停等待人工确认**

**不提交。** 汇报通过率、残留列表。

---

# Task Group 3: P0-4 - HookManager 有界队列 + 观测者 + 优雅关闭

**目标：** 引入有界 Channel 队列、异常事件、指标、优雅关闭。解决 Fire-and-Forget 异常吞噬和无界并发问题。

## Task 3.1: 定义可观测性接口和模型

**Files:**
- Create: `src/Seeing.Agent/Core/Hooks/HookExceptionEventArgs.cs`
- Create: `src/Seeing.Agent/Core/Hooks/HookDroppedEventArgs.cs`
- Create: `src/Seeing.Agent/Core/Hooks/HookMetrics.cs`
- Create: `src/Seeing.Agent/Core/Hooks/HookQueueOptions.cs`

- [ ] **Step 1: 创建 HookExceptionEventArgs**

Create file `src/Seeing.Agent/Core/Hooks/HookExceptionEventArgs.cs`:

```csharp
namespace Seeing.Agent.Core.Hooks;

public sealed class HookExceptionEventArgs : EventArgs
{
    public string HookPoint { get; }
    public string SessionId { get; }
    public Exception Exception { get; }
    public DateTimeOffset OccurredAt { get; }

    public HookExceptionEventArgs(string hookPoint, string sessionId, Exception exception)
    {
        HookPoint = hookPoint;
        SessionId = sessionId;
        Exception = exception;
        OccurredAt = DateTimeOffset.Now;
    }
}
```

- [ ] **Step 2: 创建 HookDroppedEventArgs**

Create file `src/Seeing.Agent/Core/Hooks/HookDroppedEventArgs.cs`:

```csharp
namespace Seeing.Agent.Core.Hooks;

public sealed class HookDroppedEventArgs : EventArgs
{
    public string HookPoint { get; }
    public string SessionId { get; }
    public string Reason { get; }
    public DateTimeOffset OccurredAt { get; }

    public HookDroppedEventArgs(string hookPoint, string sessionId, string reason)
    {
        HookPoint = hookPoint;
        SessionId = sessionId;
        Reason = reason;
        OccurredAt = DateTimeOffset.Now;
    }
}
```

- [ ] **Step 3: 创建 HookMetrics**

Create file `src/Seeing.Agent/Core/Hooks/HookMetrics.cs`:

```csharp
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Hooks;

public sealed class HookMetrics
{
    private readonly ConcurrentDictionary<string, HookPointStats> _byPoint = new();

    public HookPointStats GetStats(string hookPoint)
        => _byPoint.GetOrAdd(hookPoint, _ => new HookPointStats());

    public IReadOnlyDictionary<string, HookPointStats> Snapshot() => _byPoint;

    internal void IncrementSuccess(string point, TimeSpan elapsed)
    {
        var s = GetStats(point);
        Interlocked.Increment(ref s.Successes);
        Interlocked.Add(ref s.TotalElapsedTicks, elapsed.Ticks);
    }

    internal void IncrementFailed(string point)
        => Interlocked.Increment(ref GetStats(point).Failures);

    internal void IncrementDropped(string point)
        => Interlocked.Increment(ref GetStats(point).Dropped);
}

public sealed class HookPointStats
{
    public long Successes;
    public long Failures;
    public long Dropped;
    public long TotalElapsedTicks;

    public TimeSpan AverageElapsed => Successes > 0
        ? TimeSpan.FromTicks(TotalElapsedTicks / Successes)
        : TimeSpan.Zero;
}
```

- [ ] **Step 4: 创建 HookQueueOptions**

Create file `src/Seeing.Agent/Core/Hooks/HookQueueOptions.cs`:

```csharp
using System.Threading.Channels;

namespace Seeing.Agent.Core.Hooks;

public sealed class HookQueueOptions
{
    public int QueueCapacity { get; init; } = 1000;
    public BoundedChannelFullMode FullMode { get; init; } = BoundedChannelFullMode.DropOldest;
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
```

- [ ] **Step 5: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 3.2: 扩展 IHookManager 接口 + 重写 HookManager

**Files:**
- Modify: `src/Seeing.Agent/Core/Hooks/IHookManager.cs`
- Modify: `src/Seeing.Agent/Core/Hooks/HookManager.cs`（完整重写）

- [ ] **Step 1: 在 IHookManager 接口中追加观察者成员**

打开 `IHookManager.cs`，在接口体末尾追加:

```csharp
/// <summary>Hook 处理器发生异常时触发</summary>
event EventHandler<HookExceptionEventArgs>? HookExceptionOccurred;

/// <summary>Fire-and-Forget 钩子因队列满被丢弃时触发</summary>
event EventHandler<HookDroppedEventArgs>? HookDropped;

/// <summary>运行时指标（成功/失败/丢弃计数）</summary>
HookMetrics Metrics { get; }
```

- [ ] **Step 2: 完整重写 HookManager 实现**

Read 当前 `HookManager.cs` 全量，然后用以下代码完整替换（~200 行）:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Seeing.Agent.Core.Hooks;

public class HookManager : IHookManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, List<object>> _handlers = new();
    private readonly ConcurrentDictionary<string, Channel<HookPayload>> _queues = new();
    private readonly ConcurrentDictionary<string, Task> _consumers = new();
    private readonly ILogger<HookManager> _logger;
    private readonly HookQueueOptions _options;
    private readonly CancellationTokenSource _shutdownCts = new();
    private volatile bool _disposed;

    public HookMetrics Metrics { get; } = new();

    public event EventHandler<HookExceptionEventArgs>? HookExceptionOccurred;
    public event EventHandler<HookDroppedEventArgs>? HookDropped;

    public HookManager(ILogger<HookManager> logger, IOptions<HookQueueOptions>? options = null)
    {
        _logger = logger;
        _options = options?.Value ?? new HookQueueOptions();
    }

    public void Register(IHookHandler handler)
    {
        if (handler == null) { _logger.LogWarning("尝试注册空 Handler"); return; }
        AddHandler(handler.Spec.Point, handler);
    }

    public void RegisterMulti(IMultiHookHandler handler)
    {
        if (handler == null) return;
        foreach (var spec in handler.Specs) AddHandler(spec.Point, handler);
    }

    public bool Remove(IHookHandler handler)
        => handler != null && RemoveHandler(handler.Spec.Point, handler);

    public bool Clear(HookSpec spec)
        => _handlers.TryRemove(spec.Point, out _);

    public int Count(HookSpec spec)
        => _handlers.TryGetValue(spec.Point, out var list) ? list.Count : 0;

    public async Task<HookResult> TriggerAsync(HookPayload payload)
    {
        return payload.Spec.Policy switch
        {
            HookPolicy.Blocking => await ExecuteBlockingAsync(payload).ConfigureAwait(false),
            HookPolicy.FireAndForget => EnqueueFireAndForget(payload),
            HookPolicy.Parallel => await ExecuteParallelAsync(payload).ConfigureAwait(false),
            _ => HookResult.Success
        };
    }

    public Task<HookResult> TriggerBlockingAsync(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IDictionary<string, object?>? mutable = null,
        CancellationToken cancellationToken = default) =>
        TriggerAsync(HookPayload.Blocking(spec, sessionId, input, mutable, cancellationToken));

    public void TriggerFireAndForget(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null) =>
        _ = TriggerAsync(HookPayload.FireAndForget(spec, sessionId, input, result));

    public Task TriggerParallelAsync(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        CancellationToken cancellationToken = default) =>
        TriggerAsync(HookPayload.Parallel(spec, sessionId, input, cancellationToken));

    // --- internal ---

    private void AddHandler(string point, object handler)
    {
        var list = _handlers.GetOrAdd(point, _ => new List<object>());
        lock (list) { list.Add(handler); list.Sort((a, b) => GetPriority(a).CompareTo(GetPriority(b))); }
    }

    private bool RemoveHandler(string point, object handler)
    {
        if (!_handlers.TryGetValue(point, out var list)) return false;
        lock (list) return list.Remove(handler);
    }

    private static int GetPriority(object handler) =>
        handler is IHookHandler h ? h.Priority :
        handler is IMultiHookHandler m ? m.Priority : 0;

    private List<object> GetHandlers(string point)
    {
        if (!_handlers.TryGetValue(point, out var list)) return new();
        lock (list) return list.ToList();
    }

    private async Task<HookResult> ExecuteBlockingAsync(HookPayload payload)
    {
        foreach (var handler in GetHandlers(payload.Spec.Point))
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var result = await ExecuteHandlerAsync(handler, payload).ConfigureAwait(false);
                Metrics.IncrementSuccess(payload.Spec.Point, sw.Elapsed);
                if (!result.Continue) return result;
            }
            catch (Exception ex)
            {
                Metrics.IncrementFailed(payload.Spec.Point);
                RaiseException(payload, ex);
                return HookResult.FromError(ex);
            }
        }
        return HookResult.Success;
    }

    private HookResult EnqueueFireAndForget(HookPayload payload)
    {
        var handlers = GetHandlers(payload.Spec.Point);
        if (handlers.Count == 0) return HookResult.Success;

        var queue = _queues.GetOrAdd(payload.Spec.Point, point =>
        {
            var ch = Channel.CreateBounded<HookPayload>(new BoundedChannelOptions(_options.QueueCapacity)
            {
                FullMode = _options.FullMode, SingleReader = true, SingleWriter = false
            });
            _consumers[point] = Task.Run(() => ConsumeAsync(point, ch, _shutdownCts.Token));
            return ch;
        });

        if (!queue.Writer.TryWrite(payload))
        {
            Metrics.IncrementDropped(payload.Spec.Point);
            HookDropped?.Invoke(this, new(payload.Spec.Point, payload.SessionId, "队列已满"));
        }
        return HookResult.Success;
    }

    private async Task ConsumeAsync(string point, Channel<HookPayload> queue, CancellationToken ct)
    {
        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var handler in GetHandlers(point))
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        await ExecuteHandlerAsync(handler, payload).ConfigureAwait(false);
                        Metrics.IncrementSuccess(point, sw.Elapsed);
                    }
                    catch (Exception ex)
                    {
                        Metrics.IncrementFailed(point);
                        RaiseException(payload, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<HookResult> ExecuteParallelAsync(HookPayload payload)
    {
        await Task.WhenAll(GetHandlers(payload.Spec.Point).Select(h => SafeExecuteAsync(h, payload)));
        return HookResult.Success;
    }

    private async Task SafeExecuteAsync(object handler, HookPayload payload)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            await ExecuteHandlerAsync(handler, payload).ConfigureAwait(false);
            Metrics.IncrementSuccess(payload.Spec.Point, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Metrics.IncrementFailed(payload.Spec.Point);
            RaiseException(payload, ex);
        }
    }

    private static async Task<HookResult> ExecuteHandlerAsync(object handler, HookPayload payload)
    {
        if (handler is IHookHandler h) return await h.ExecuteAsync(payload).ConfigureAwait(false);
        if (handler is IMultiHookHandler m) return await m.ExecuteAsync(payload).ConfigureAwait(false);
        return HookResult.Success;
    }

    private void RaiseException(HookPayload payload, Exception ex)
    {
        try { HookExceptionOccurred?.Invoke(this, new(payload.Spec.Point, payload.SessionId, ex)); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var q in _queues.Values) q.Writer.TryComplete();
        var drainCts = new CancellationTokenSource(_options.ShutdownDrainTimeout);
        try { await Task.WhenAll(_consumers.Values).WaitAsync(drainCts.Token); }
        catch (OperationCanceledException) { _shutdownCts.Cancel(); }
        finally { drainCts.Dispose(); _shutdownCts.Dispose(); }
    }
}
```

**⚠️ 关键：构造函数签名从 `HookManager(ILogger<HookManager>)` 改为 `HookManager(ILogger<HookManager>, IOptions<HookQueueOptions>?)`。所有 DI 注册和测试必须对应更新。**

- [ ] **Step 3: 更新 DI 注册**

在 `ServiceCollectionExtensions.RegisterCoreServices` 中（约 309 行）:

```csharp
services.AddSingleton<HookManager>();
services.AddSingleton<Seeing.Agent.Core.Hooks.IHookManager>(sp =>
    sp.GetRequiredService<HookManager>());
```

保持不变（`IOptions<HookQueueOptions>` 通过 DI 自动注入）。追加默认选项注册:

```csharp
services.AddOptions<HookQueueOptions>();
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 3.3: 补充 HookManager 单元测试

**Files:**
- Modify: `tests/Seeing.Agent.Tests/Hooks/HookManagerTests.cs`（追加新测试）

- [ ] **Step 1: 添加异常事件和丢弃事件测试**

在现有 `HookManagerTests.cs` 末尾追加:

```csharp
[Fact]
public async Task BlockingHandlerThrows_ShouldRaiseExceptionEvent()
{
    var logger = new Mock<ILogger<HookManager>>();
    var manager = new HookManager(logger.Object);
    HookExceptionEventArgs? captured = null;
    manager.HookExceptionOccurred += (_, e) => captured = e;

    var handler = new Mock<IHookHandler>();
    handler.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
    handler.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
        .ThrowsAsync(new InvalidOperationException("test error"));
    manager.Register(handler.Object);

    var result = await manager.TriggerBlockingAsync(
        HookRegistry.ToolExecuteBefore, "session-1");

    result.Continue.Should().BeFalse();
    captured.Should().NotBeNull();
    captured!.HookPoint.Should().Be("tool.execute.before");
    captured.Exception.Should().BeOfType<InvalidOperationException>();
    manager.Metrics.GetStats("tool.execute.before").Failures.Should().Be(1);
}

[Fact]
public async Task FireAndForget_QueueFull_ShouldRaiseDroppedEvent()
{
    var logger = new Mock<ILogger<HookManager>>();
    var options = Options.Create(new HookQueueOptions
    {
        QueueCapacity = 1,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    var manager = new HookManager(logger.Object, options);
    HookDroppedEventArgs? dropped = null;
    manager.HookDropped += (_, e) => dropped = e;

    var block = new TaskCompletionSource();
    var handler = new Mock<IHookHandler>();
    handler.Setup(h => h.Spec).Returns(HookRegistry.SessionUpdated);
    handler.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
        .Returns(async () => { await block.Task; return HookResult.Success; });
    manager.Register(handler.Object);

    manager.TriggerFireAndForget(HookRegistry.SessionUpdated, "s1");
    await Task.Delay(50);
    manager.TriggerFireAndForget(HookRegistry.SessionUpdated, "s2");
    manager.TriggerFireAndForget(HookRegistry.SessionUpdated, "s3");
    await Task.Delay(100);

    dropped.Should().NotBeNull();

    block.SetResult();
    await ((IAsyncDisposable)manager).DisposeAsync();
}
```

- [ ] **Step 2: 运行 Hooks 测试**

Run:
```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug --filter "FullyQualifiedName~Hooks"
```
Expected: 全部通过（含新增 2 个）。

- [ ] **Step 3: 全量测试冒烟**

Run:
```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug
```

- [ ] **Step 4: 暂停等待人工确认**

**不提交。** 汇报新增文件清单、测试通过数。

---

# Task Group 4: P0-5 - 装饰器链动态化 + ToolPolicies + UI 集成（档次 2）

**目标：** 引入 `ToolPolicies` 配置节，装饰器改为每次 `ExecuteAsync` 时按策略动态构建，ToolsPage 卡片加"策略"按钮。

## Task 4.1: 定义 ToolPolicies 配置模型

**Files:**
- Create: `src/Seeing.Agent/Configuration/ToolPoliciesOptions.cs`
- Modify: `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs`
- Modify: `src/Seeing.Agent/Configuration/UnifiedConfigManager.cs`

- [ ] **Step 1: 创建 ToolPoliciesOptions**

Create file `src/Seeing.Agent/Configuration/ToolPoliciesOptions.cs`:

```csharp
namespace Seeing.Agent.Configuration;

public sealed class ToolPoliciesOptions
{
    public ToolExecutionPolicy Default { get; set; } = new();

    public Dictionary<string, ToolExecutionPolicy> Overrides { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ToolExecutionPolicy
{
    public int? TimeoutSeconds { get; set; }
    public int? MaxRetries { get; set; }
    public int? RetryDelayMs { get; set; }
    public bool? CacheEnabled { get; set; }
    public int? CacheTtlMinutes { get; set; }
}
```

- [ ] **Step 2: 在 SeeingAgentOptions 追加属性**

在 `SeeingAgentOptions.cs` 末尾追加（约 77 行前）:
```csharp
/// <summary>工具执行策略配置</summary>
public ToolPoliciesOptions ToolPolicies { get; set; } = new();
```

- [ ] **Step 3: 在 UnifiedConfigManager 注册新配置节**

在 `BuildSectionRegistry()` 方法内追加:
```csharp
["ToolPolicies"] = new("ToolPolicies", "seeing.json", ConfigScope.Both,
    typeof(ToolPoliciesOptions), displayName: "工具执行策略", displayOrder: 18),
```

在 `GetFromSeeingAgent<T>()` 方法追加 case:
```csharp
"ToolPolicies" => SeeingAgent.ToolPolicies as T,
```

在 `UpdateSeeingAgentProperty()` 方法追加 case:
```csharp
case "ToolPolicies":
    if (value is ToolPoliciesOptions p) SeeingAgent.ToolPolicies = p;
    break;
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 4.2: 创建策略解析器 + 动态 Pipeline

**Files:**
- Create: `src/Seeing.Agent/Decorators/IToolPolicyResolver.cs`
- Create: `src/Seeing.Agent/Decorators/DefaultToolPolicyResolver.cs`
- Create: `src/Seeing.Agent/Decorators/IToolDecoratorPipeline.cs`
- Create: `src/Seeing.Agent/Decorators/DynamicToolDecoratorPipeline.cs`

- [ ] **Step 1: 创建 IToolPolicyResolver + EffectiveToolPolicy**

Create `src/Seeing.Agent/Decorators/IToolPolicyResolver.cs`:

```csharp
namespace Seeing.Agent.Decorators;

public interface IToolPolicyResolver
{
    EffectiveToolPolicy Resolve(string toolId);
}

public sealed record EffectiveToolPolicy(
    TimeSpan Timeout,
    int MaxRetries,
    TimeSpan RetryDelay,
    bool CacheEnabled,
    TimeSpan CacheTtl)
{
    public static EffectiveToolPolicy Fallback => new(
        TimeSpan.FromSeconds(30), 3, TimeSpan.FromSeconds(1), true, TimeSpan.FromMinutes(5));
}
```

- [ ] **Step 2: 创建 DefaultToolPolicyResolver**

Create `src/Seeing.Agent/Decorators/DefaultToolPolicyResolver.cs`:

```csharp
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Decorators;

public sealed class DefaultToolPolicyResolver : IToolPolicyResolver
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;

    public DefaultToolPolicyResolver(IOptionsMonitor<SeeingAgentOptions> options)
        => _options = options;

    public EffectiveToolPolicy Resolve(string toolId)
    {
        var policies = _options.CurrentValue.ToolPolicies ?? new();
        var def = policies.Default ?? new();
        var ovr = policies.Overrides?.TryGetValue(toolId, out var o) == true ? o : null;
        var fb = EffectiveToolPolicy.Fallback;

        return new EffectiveToolPolicy(
            TimeSpan.FromSeconds(ovr?.TimeoutSeconds  ?? def.TimeoutSeconds  ?? (int)fb.Timeout.TotalSeconds),
            ovr?.MaxRetries                            ?? def.MaxRetries      ?? fb.MaxRetries,
            TimeSpan.FromMilliseconds(ovr?.RetryDelayMs ?? def.RetryDelayMs   ?? (int)fb.RetryDelay.TotalMilliseconds),
            ovr?.CacheEnabled                          ?? def.CacheEnabled    ?? fb.CacheEnabled,
            TimeSpan.FromMinutes(ovr?.CacheTtlMinutes  ?? def.CacheTtlMinutes ?? (int)fb.CacheTtl.TotalMinutes));
    }
}
```

- [ ] **Step 3: 创建 IToolDecoratorPipeline + DynamicToolDecoratorPipeline**

Create `src/Seeing.Agent/Decorators/IToolDecoratorPipeline.cs`:

```csharp
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Decorators;

public interface IToolDecoratorPipeline
{
    ITool Build(ITool rawTool, EffectiveToolPolicy policy);
}
```

Create `src/Seeing.Agent/Decorators/DynamicToolDecoratorPipeline.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Decorators;

public sealed class DynamicToolDecoratorPipeline : IToolDecoratorPipeline
{
    private readonly IMemoryCache? _cache;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicToolDecoratorPipeline(ILoggerFactory loggerFactory, IMemoryCache? cache = null)
    {
        _loggerFactory = loggerFactory;
        _cache = cache;
    }

    public ITool Build(ITool rawTool, EffectiveToolPolicy policy)
    {
        ITool current = rawTool;
        // 顺序: Cache(最内层) -> Retry -> Timeout(最外层)
        if (policy.CacheEnabled && _cache != null)
            current = new CachedToolDecorator(current, _cache, policy.CacheTtl,
                _loggerFactory.CreateLogger<CachedToolDecorator>());
        if (policy.MaxRetries > 1)
            current = new RetryToolDecorator(current, policy.MaxRetries, policy.RetryDelay,
                logger: _loggerFactory.CreateLogger<RetryToolDecorator>());
        current = new TimeoutToolDecorator(current, policy.Timeout,
            _loggerFactory.CreateLogger<TimeoutToolDecorator>());
        return current;
    }
}
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 4.3: 修改 ToolManager 支持动态装饰器 + 修复 TimeoutToolContext

**Files:**
- Modify: `src/Seeing.Agent/Tools/ToolManager.cs`（构造函数 + ExecuteAsync + RegisterToolAsync）
- Modify: `src/Seeing.Agent/Decorators/TimeoutToolDecorator.cs`（TimeoutToolContext 修复）
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（DI 注册）

- [ ] **Step 1: 修复 TimeoutToolContext 字段丢失**

在 `TimeoutToolDecorator.cs` 中将私有类 `TimeoutToolContext` 的手工字段拷贝改为反射:

```csharp
private sealed class TimeoutToolContext : ToolContext
{
    public TimeoutToolContext(ToolContext original, CancellationToken timeoutToken)
    {
        foreach (var prop in typeof(ToolContext).GetProperties()
            .Where(p => p.CanRead && p.CanWrite))
        {
            prop.SetValue(this, prop.GetValue(original));
        }
        CancellationToken = timeoutToken;
    }
}
```

- [ ] **Step 2: 修改 ToolManager 构造函数**

在 `ToolManager.cs` 构造函数增加 `IToolDecoratorPipeline?` 和 `IToolPolicyResolver?` 参数:

```csharp
private readonly IToolDecoratorPipeline? _decoratorPipeline;
private readonly IToolPolicyResolver? _policyResolver;

public ToolManager(
    ILogger<ToolManager> logger,
    Core.Hooks.IHookManager hookManager,
    IServiceProvider? serviceProvider = null,
    IToolDecoratorRegistry? decoratorRegistry = null,
    IToolDecoratorPipeline? decoratorPipeline = null,
    IToolPolicyResolver? policyResolver = null,
    IRuleEvaluator? ruleEvaluator = null,
    IWorkspaceProvider? workspace = null)
{
    // ...existing fields...
    _decoratorPipeline = decoratorPipeline;
    _policyResolver = policyResolver;
}
```

- [ ] **Step 3: 修改 RegisterToolAsync 存原始工具**

在 `RegisterToolAsync` (约 358 行)，将:
```csharp
var finalTool = _decoratorRegistry?.Apply(tool) ?? tool;
_tools[tool.Id] = finalTool;
```

改为:
```csharp
if (_decoratorPipeline != null && _policyResolver != null)
    _tools[tool.Id] = tool; // 存原始，执行时动态构建
else
    _tools[tool.Id] = _decoratorRegistry?.Apply(tool) ?? tool;
```

- [ ] **Step 4: 修改 ExecuteAsync 动态构建装饰器**

在 `ExecuteAsync` (约 486 行) 中，获取工具后插入:

```csharp
if (!_tools.TryGetValue(toolId, out var rawTool))
    return new ToolResult { ... };

// 动态构建装饰器链
var tool = _decoratorPipeline != null && _policyResolver != null
    ? _decoratorPipeline.Build(rawTool, _policyResolver.Resolve(toolId))
    : rawTool;
```

后续 `tool.ExecuteAsync(...)` 使用新变量 `tool`。

- [ ] **Step 5: 更新 DI 注册**

在 `ServiceCollectionExtensions.RegisterCoreServices` 中替换装饰器注册:

```csharp
// 动态装饰器 Pipeline
services.AddSingleton<IToolPolicyResolver, DefaultToolPolicyResolver>();
services.AddSingleton<IToolDecoratorPipeline, DynamicToolDecoratorPipeline>();
// 兼容旧路径
services.AddSingleton<IToolDecoratorRegistry>(sp => new ToolDecoratorRegistry(sp));
```

修改 `ToolManager` 注册:

```csharp
services.AddSingleton<ToolManager>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ToolManager>>();
    var hookManager = sp.GetRequiredService<Seeing.Agent.Core.Hooks.IHookManager>();
    var tools = sp.GetServices<ITool>();
    var pipeline = sp.GetService<IToolDecoratorPipeline>();
    var resolver = sp.GetService<IToolPolicyResolver>();
    var workspace = sp.GetService<IWorkspaceProvider>();

    var invoker = new ToolManager(logger, hookManager, sp,
        decoratorPipeline: pipeline, policyResolver: resolver, workspace: workspace);

    foreach (var tool in tools) invoker.RegisterTool(tool);
    return invoker;
});
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 4.4: 补充 Resolver / Pipeline 单元测试

**Files:**
- Create: `tests/Seeing.Agent.Tests/Decorators/DefaultToolPolicyResolverTests.cs`
- Create: `tests/Seeing.Agent.Tests/Decorators/DynamicToolDecoratorPipelineTests.cs`

- [ ] **Step 1: Resolver 测试**

Create `tests/Seeing.Agent.Tests/Decorators/DefaultToolPolicyResolverTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Decorators;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

public class DefaultToolPolicyResolverTests
{
    [Fact]
    public void NoConfig_ReturnsFallback()
    {
        var resolver = new DefaultToolPolicyResolver(new StubMonitor(new()));
        resolver.Resolve("any").Should().BeEquivalentTo(EffectiveToolPolicy.Fallback);
    }

    [Fact]
    public void OverrideMergesWithDefault()
    {
        var opts = new SeeingAgentOptions
        {
            ToolPolicies = new ToolPoliciesOptions
            {
                Default = new ToolExecutionPolicy { TimeoutSeconds = 60, MaxRetries = 5 },
                Overrides = new(StringComparer.OrdinalIgnoreCase) { ["bash"] = new() { TimeoutSeconds = 300 } }
            }
        };
        var resolver = new DefaultToolPolicyResolver(new StubMonitor(opts));
        resolver.Resolve("bash").Timeout.Should().Be(TimeSpan.FromSeconds(300));
        resolver.Resolve("read").Timeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ToolIdIsCaseInsensitive()
    {
        var opts = new SeeingAgentOptions
        {
            ToolPolicies = new ToolPoliciesOptions
            {
                Overrides = new(StringComparer.OrdinalIgnoreCase) { ["Bash"] = new() { TimeoutSeconds = 100 } }
            }
        };
        var resolver = new DefaultToolPolicyResolver(new StubMonitor(opts));
        resolver.Resolve("bash").Timeout.Should().Be(TimeSpan.FromSeconds(100));
    }

    private class StubMonitor : IOptionsMonitor<SeeingAgentOptions>
    {
        private readonly SeeingAgentOptions _v;
        public StubMonitor(SeeingAgentOptions v) => _v = v;
        public SeeingAgentOptions CurrentValue => _v;
        public SeeingAgentOptions Get(string? n) => _v;
        public IDisposable? OnChange(Action<SeeingAgentOptions, string?> l) => null;
    }
}
```

- [ ] **Step 2: Pipeline 测试**

Create `tests/Seeing.Agent.Tests/Decorators/DynamicToolDecoratorPipelineTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Decorators;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

public class DynamicToolDecoratorPipelineTests
{
    [Fact]
    public void Build_ShouldWrapInTimeoutAsOutermost()
    {
        var pipeline = new DynamicToolDecoratorPipeline(NullLoggerFactory.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        var result = pipeline.Build(new StubTool(), EffectiveToolPolicy.Fallback);
        result.Should().BeOfType<TimeoutToolDecorator>();
    }

    [Fact]
    public void Build_CacheDisabled_ShouldSkipCache()
    {
        var pipeline = new DynamicToolDecoratorPipeline(NullLoggerFactory.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        var raw = new StubTool();
        var policy = EffectiveToolPolicy.Fallback with { CacheEnabled = false };
        var result = pipeline.Build(raw, policy);
        ((ToolDecorator)result).GetInnermostTool().Should().BeSameAs(raw);
    }

    private class StubTool : ITool
    {
        public string Id => "stub";
        public string Description => "";
        public System.Text.Json.JsonElement ParametersSchema => new();
        public Task<Seeing.Agent.Core.Models.ToolResult> ExecuteAsync(
            System.Text.Json.JsonElement a, ToolContext c) => Task.FromResult(
                new Seeing.Agent.Core.Models.ToolResult { Success = true });
    }
}
```

- [ ] **Step 3: 运行 Decorator 测试**

Run:
```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug --filter "FullyQualifiedName~Decorator"
```
Expected: 全部通过。

## Task 4.5: ToolsPage.razor 集成策略编辑 UI

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/ToolsPage.razor`（每个卡片加"策略"按钮 + Modal）
- Option: Create `samples/Seeing.Agent.WebUI/Components/ToolPolicyEditor.razor`

- [ ] **Step 1: 阅读现有 ToolsPage 结构**

Read `samples/Seeing.Agent.WebUI/Pages/ToolsPage.razor` 全量，找到卡片渲染循环（`@foreach` 渲染 `Card` 的区域）以及 `@code` 块。

- [ ] **Step 2: 确认依赖注入可用性**

在 `ToolsPage.razor` code 块检查 `@inject` 列表。如果没有 `UnifiedConfigManager` 和 `IToolPolicyResolver`，追加:

```razor
@inject Seeing.Agent.Configuration.UnifiedConfigManager ConfigManager
@inject Seeing.Agent.Decorators.IToolPolicyResolver PolicyResolver
```

- [ ] **Step 3: 在每个工具卡片内加"策略"按钮**

在卡片渲染的 `CardAction` 或 `Card` 内容区追加:

```razor
<CardActions>
    <Button OnClick="@(() => OpenPolicyEditor(tool.Id))"
            Icon="@IconType.Outline.Setting" Size="small">
        策略
    </Button>
</CardActions>
```

- [ ] **Step 4: 实现 Modal 弹出编辑**

在 `@code` 块追加:

```csharp
private string? _editingToolId;
private ToolExecutionPolicy? _editingPolicy;
private bool _policyModalVisible;
private ConfigLevel _targetLevel = ConfigLevel.Project;

private async Task OpenPolicyEditor(string toolId)
{
    _editingToolId = toolId;
    var opts = ConfigManager.GetSeeingAgentOptions();
    var policies = opts.ToolPolicies ?? new();
    _editingPolicy = policies.Overrides?.TryGetValue(toolId, out var o) == true
        ? new ToolExecutionPolicy
        {
            TimeoutSeconds = o.TimeoutSeconds,
            MaxRetries = o.MaxRetries,
            RetryDelayMs = o.RetryDelayMs,
            CacheEnabled = o.CacheEnabled,
            CacheTtlMinutes = o.CacheTtlMinutes
        }
        : new ToolExecutionPolicy();
    _policyModalVisible = true;
    StateHasChanged();
}

private async Task SavePolicy()
{
    if (_editingToolId == null || _editingPolicy == null) return;

    var opts = ConfigManager.GetSeeingAgentOptions();
    opts.ToolPolicies ??= new();
    opts.ToolPolicies.Overrides ??= new(StringComparer.OrdinalIgnoreCase);
    opts.ToolPolicies.Overrides[_editingToolId] = _editingPolicy;

    await ConfigManager.SaveSectionAsync("ToolPolicies", opts.ToolPolicies, _targetLevel);
    _policyModalVisible = false;
    await MessageService.Success("策略已保存，下次工具调用生效");
}

// 显示当前生效值
private EffectiveToolPolicy? GetEffectivePolicy(string toolId)
    => PolicyResolver.Resolve(toolId);
```

在 `@code` 区域追加 Modal 模板（使用 AntDesign 的 `<Modal>`）:

```razor
<Modal Title="@($"工具策略 - {_editingToolId}")"
       Visible="@_policyModalVisible"
       OnOk="SavePolicy" OnCancel="@(() => _policyModalVisible = false)"
       OkText="保存" CancelText="取消">
    <Form Model="_editingPolicy">
        <FormItem Label="超时（秒，留空=默认）">
            <InputNumber @bind-Value="_editingPolicy!.TimeoutSeconds" Min="1" Max="3600" />
        </FormItem>
        <FormItem Label="最大重试次数">
            <InputNumber @bind-Value="_editingPolicy!.MaxRetries" Min="1" Max="20" />
        </FormItem>
        <FormItem Label="重试间隔（毫秒）">
            <InputNumber @bind-Value="_editingPolicy!.RetryDelayMs" Min="100" Max="60000" />
        </FormItem>
        <FormItem Label="启用缓存">
            <Select @bind-Value="_editingPolicy!.CacheEnabled" TItemValue="bool?" TItem="string" AllowClear>
                <SelectOption Value="true" Label="启用" />
                <SelectOption Value="false" Label="禁用" />
            </Select>
        </FormItem>
        <FormItem Label="缓存 TTL（分钟）">
            <InputNumber @bind-Value="_editingPolicy!.CacheTtlMinutes" Min="1" Max="1440" />
        </FormItem>
        <FormItem Label="保存级别">
            <Select @bind-Value="_targetLevel" TItemValue="ConfigLevel" TItem="string">
                <SelectOption Value="ConfigLevel.Project" Label="项目级" />
                <SelectOption Value="ConfigLevel.User" Label="用户级" />
            </Select>
        </FormItem>
    </Form>
    @if (_editingToolId != null)
    {
        var eff = GetEffectivePolicy(_editingToolId);
        <Divider />
        <p><strong>当前生效值：</strong></p>
        <p>超时 @eff.Timeout.TotalSeconds 秒 | 重试 @eff.MaxRetries 次 | 缓存 @(eff.CacheEnabled ? "启用" : "禁用")</p>
    }
</Modal>
```

- [ ] **Step 5: 编译 WebUI**

Run:
```bash
dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj -c Debug
```
Expected: 编译成功。

- [ ] **Step 6: 全量测试冒烟**

Run:
```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug
```

- [ ] **Step 7: 暂停等待人工确认**

**不提交。** 汇报 UI 改动位置、需手工验证的 `.seeing/seeing.json` 示例。

---

# Task Group 5: P0-2 - Agent 四层职责分离

**目标：** 将 `AgentManager` (899 行，单类三接口) 拆分为 Store / ConfigLoader / RuntimeManager / Registry 四层职责清晰分离。

**拆分原则：**
- `IAgentStore`：已独立（`AgentStore.cs`，89 行），保持不变。
- `IAgentRuntimeManager`：已独立（`AgentRuntimeManager.cs`，~400 行），保持不变。
- `IAgentConfigLoader`：**新增**，负责 YAML/MD 文件加载 + 热更新。
- `IAgentRegistry`：接口已有，实现改为**薄门面**（仅聚合 Store + ConfigLoader + RuntimeManager 的查询）。
- `IAgentManager` 接口：保留（向后兼容），实现改为门面委托。

## Task 5.1: 抽取 IAgentConfigLoader 接口和实现

**Files:**
- Create: `src/Seeing.Agent/Configuration/IAgentConfigLoader.cs`
- Create: `src/Seeing.Agent/Configuration/AgentConfigLoader.cs`

- [ ] **Step 1: 创建 IAgentConfigLoader 接口**

Create `src/Seeing.Agent/Configuration/IAgentConfigLoader.cs`:

```csharp
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Configuration;

/// <summary>
/// Agent MD/YAML 配置文件加载器。
/// 职责：从文件系统发现、解析、热更新 MD 配置。
/// </summary>
public interface IAgentConfigLoader
{
    Task<IReadOnlyList<AgentMdInfo>> GetAllMdInfoAsync(CancellationToken ct = default);
    Task<string?> GetMdContentAsync(string name, ConfigLevel level, CancellationToken ct = default);
    Task<AgentEditModel> CreateMdConfigAsync(string name, ConfigLevel level, CancellationToken ct = default);
    Task<bool> SaveMdContentAsync(string name, ConfigLevel level, string content, CancellationToken ct = default);
    Task<bool> DeleteMdConfigAsync(string name, ConfigLevel level, CancellationToken ct = default);
    string GetMdFilePath(string name, ConfigLevel level);
    string GetDefaultMdTemplate(string agentName);
    string GenerateMdContent(AgentEditModel model);
    Task<IReadOnlyList<AgentEditModel>> GetAllEditModelsAsync(CancellationToken ct = default);
    Task<AgentEditModel?> GetEditModelAsync(string agentName, CancellationToken ct = default);

    /// <summary>将 MD 配置应用到 AgentStore</summary>
    Task ApplyMdConfigToStoreAsync(string name, AgentConfigFile config, IAgentStore store);
    Task RemoveMdConfigFromStoreAsync(string name, IAgentStore store);

    event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;
}
```

- [ ] **Step 2: 从 AgentManager 中剪切相关代码到 AgentConfigLoader**

从 `AgentManager.cs` 中**剪切**以下方法到 `AgentConfigLoader.cs`:
- `GetAllMdInfoAsync`
- `GetMdContentAsync`
- `CreateMdConfigAsync`
- `SaveMdContentAsync`
- `DeleteMdConfigAsync`
- `GetMdFilePath`
- `GetDefaultMdTemplate`
- `GenerateMdContent`
- `GetAllEditModelsAsync`
- `GetEditModelAsync`
- `SaveEditModelAsync`
- `SetAgentDisabledAsync`
- `ApplyMdConfigToStoreAsync`
- `RemoveMdConfigFromStoreAsync`
- `ParseFile`
- `ParseContent`
- `ConvertToEditModel`
- `ConvertToMdContent`
- `CreateAgentMdInfo`
- `OnConfigChanged`
- `_configCache` / `_yamlDeserializer` / `_yamlSerializer` 字段

**保留在 AgentManager 的**：构造函数、StartAsync、StopAsync、`GetAgentsAsync`/`GetAgentAsync`/`RegisterAgentAsync` 等编排方法（改为委托给 Store + ConfigLoader）。

- [ ] **Step 3: 编译验证 + 修复编译错误**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`
修复：类型不匹配、using 缺少等问题。

## Task 5.2: 创建 AgentRegistry（薄门面）

**Files:**
- Create: `src/Seeing.Agent/Core/AgentRegistry.cs`

- [ ] **Step 1: 创建 AgentRegistry 薄门面**

Create `src/Seeing.Agent/Core/AgentRegistry.cs`:

```csharp
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core;

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly IAgentStore _store;
    private readonly IAgentRuntimeManager _runtime;

    public AgentRegistry(IAgentStore store, IAgentRuntimeManager runtime)
    {
        _store = store;
        _runtime = runtime;
    }

    public Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync() => _store.GetAllAsync();
    public Task<AgentDefinition?> GetAgentAsync(string name) => _store.GetAsync(name);

    public Task<AgentDefinition?> GetAgentWithMergedConfigAsync(string name, string? provider = null, string? model = null)
        => _store.GetAsync(name);

    public Task<IReadOnlyList<AgentDefinition>> GetSubAgentsAsync()
        => Task.FromResult<IReadOnlyList<AgentDefinition>>(
            Task.Run(async () => await _store.GetAllAsync()).Result
                .Where(a => a.Mode == AgentMode.SubAgent && !a.Disabled).ToList());

    public Task<IReadOnlyList<AgentDefinition>> GetTaskableAgentsAsync()
        => Task.FromResult<IReadOnlyList<AgentDefinition>>(
            Task.Run(async () => await _store.GetAllAsync()).Result
                .Where(a => !a.Disabled && a.Mode != AgentMode.Primary && a.Runtime == AgentRuntime.Native).ToList());

    public Task<IReadOnlyList<AgentDefinition>> GetPrimaryAgentsAsync()
        => Task.FromResult<IReadOnlyList<AgentDefinition>>(
            Task.Run(async () => await _store.GetAllAsync()).Result
                .Where(a => (a.Mode == AgentMode.Primary || a.Mode == AgentMode.All) && !a.IsHidden && !a.Disabled).ToList());

    public async Task<string> GetDefaultAgentNameAsync() => (await _runtime.GetDefaultAgentNameAsync()) ?? "build";
    public Task SetDefaultAgentAsync(string name) => _runtime.SetDefaultAgentAsync(name);
    public Task UpdateAgentModelAsync(string agentName, ModelReference model) => _runtime.UpdateAgentModelAsync(agentName, model);
    public Task<ModelReference?> GetEffectiveModelAsync(string agentName) => _runtime.GetEffectiveModelAsync(agentName);
    public Task RegisterAgentAsync(AgentDefinition agentInfo) => _store.RegisterAsync(agentInfo);
    public bool UnregisterAgent(string name) => _store.Unregister(name);
    public bool HasAgent(string name) => _store.Has(name);

    public Task<IReadOnlyList<AgentDefinition>> GetAccessibleSubAgentsAsync(IReadOnlyList<PermissionRuleEntry> callerPermissions)
    {
        // 委托给现有逻辑（从 AgentManager 搬移）
        return Task.FromResult<IReadOnlyList<AgentDefinition>>(Array.Empty<AgentDefinition>());
    }

    public IAgent? GetOrCreateAgentInstance(string name)
    {
        var info = Task.Run(async () => await _store.GetAsync(name)).Result;
        return info != null ? new AgentInfoWrapper(info) : null;
    }

    // AgentInfoWrapper 从 AgentManager 搬移过来
    private sealed class AgentInfoWrapper : IAgent { /* 从 AgentManager 复制 */ }
}
```

**注意**：`GetSubAgentsAsync` / `GetPrimaryAgentsAsync` / `GetTaskableAgentsAsync` 目前用了 `.Result` 同步阻塞。若 `IAgentStore.GetAllAsync()` 是纯内存操作，可改为同步包装；否则需改为 `async` 实现。

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj -c Debug`

## Task 5.3: 精简 AgentManager 为门面

**Files:**
- Modify: `src/Seeing.Agent/Configuration/AgentManager.cs`（大量删除，改为委托）

- [ ] **Step 1: 重写 AgentManager 为纯门面**

将 `AgentManager` 从 899 行缩减到约 120 行:

```csharp
public class AgentManager : IAgentManager, IAgentRegistry, IHostedService
{
    private readonly IAgentStore _store;
    private readonly IAgentRuntimeManager _runtime;
    private readonly IAgentConfigLoader _configLoader;
    private readonly AgentRegistry _registry;
    private readonly ILogger<AgentManager> _logger;
    private readonly IWorkspaceProvider _workspace;
    private readonly IOptions<SeeingAgentOptions>? _options;

    public event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;

    public AgentManager(
        ILogger<AgentManager> logger,
        IAgentStore store,
        IAgentRuntimeManager runtime,
        IAgentConfigLoader configLoader,
        IWorkspaceProvider workspace,
        IOptions<SeeingAgentOptions>? options = null)
    {
        _store = store;
        _runtime = runtime;
        _configLoader = configLoader;
        _workspace = workspace;
        _options = options;
        _logger = logger;
        _registry = new AgentRegistry(store, runtime);

        _configLoader.ConfigChanged += (_, e) => ConfigChanged?.Invoke(this, e);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AgentManager 启动");
        await _runtime.InitializeAsync().ConfigureAwait(false);
        // MD 配置由 IAgentConfigLoader 管理，在 Bootstrapper 中通过 IAsyncInitializable 加载
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // 所有 IAgentManager / IAgentRegistry 方法委托给 _registry 或 _configLoader
    public Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync() => _registry.GetAgentsAsync();
    public Task<AgentDefinition?> GetAgentAsync(string name) => _registry.GetAgentAsync(name);
    // ...(其余委托同模式)...
}
```

**删除的内部类**：`AgentInfoWrapper`（挪到 AgentRegistry）。

- [ ] **Step 2: 更新 DI 注册**

在 `ServiceCollectionExtensions.cs` 中:

```csharp
// 新增注册
services.AddSingleton<IAgentConfigLoader, AgentConfigLoader>();
services.AddSingleton<AgentRegistry>();

// AgentManager 简化注册
services.AddSingleton<AgentManager>(sp => new AgentManager(
    sp.GetRequiredService<ILogger<AgentManager>>(),
    sp.GetRequiredService<IAgentStore>(),
    sp.GetRequiredService<IAgentRuntimeManager>(),
    sp.GetRequiredService<IAgentConfigLoader>(),
    sp.GetRequiredService<IWorkspaceProvider>(),
    sp.GetService<IOptions<SeeingAgentOptions>>()));

services.AddSingleton<IAgentManager>(sp => sp.GetRequiredService<AgentManager>());
services.AddSingleton<IAgentRegistry>(sp => sp.GetRequiredService<AgentRegistry>());
services.AddHostedService<AgentManager>(sp => sp.GetRequiredService<AgentManager>());
```

**关键变化**：`IAgentRegistry` 不再指向 `AgentManager`，而是指向独立的 `AgentRegistry` 单例。

- [ ] **Step 3: 全量编译 + 测试**

Run:
```bash
dotnet build Seeing.Agent.slnx -c Debug
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug
```
Expected: 全部通过。

- [ ] **Step 4: 暂停等待人工确认**

**不提交。** 汇报 AgentManager 缩减行数（899 → ~120）、新增文件清单、测试通过率。

---

---

# 附录 A: 文件变更清单

## 新增文件 (18 个)

| 文件 | 所属 Task Group |
|------|----------------|
| `src/Seeing.Agent/Extensions/Bootstrap/IAsyncInitializable.cs` | TG2 |
| `src/Seeing.Agent/Extensions/Bootstrap/SeeingAgentBootstrapper.cs` | TG2 |
| `src/Seeing.Agent/Core/Hooks/HookExceptionEventArgs.cs` | TG3 |
| `src/Seeing.Agent/Core/Hooks/HookDroppedEventArgs.cs` | TG3 |
| `src/Seeing.Agent/Core/Hooks/HookMetrics.cs` | TG3 |
| `src/Seeing.Agent/Core/Hooks/HookQueueOptions.cs` | TG3 |
| `src/Seeing.Agent/Configuration/ToolPoliciesOptions.cs` | TG4 |
| `src/Seeing.Agent/Decorators/IToolPolicyResolver.cs` | TG4 |
| `src/Seeing.Agent/Decorators/DefaultToolPolicyResolver.cs` | TG4 |
| `src/Seeing.Agent/Decorators/IToolDecoratorPipeline.cs` | TG4 |
| `src/Seeing.Agent/Decorators/DynamicToolDecoratorPipeline.cs` | TG4 |
| `tests/Seeing.Agent.Tests/Decorators/DefaultToolPolicyResolverTests.cs` | TG4 |
| `tests/Seeing.Agent.Tests/Decorators/DynamicToolDecoratorPipelineTests.cs` | TG4 |
| `src/Seeing.Agent/Configuration/IAgentConfigLoader.cs` | TG5 |
| `src/Seeing.Agent/Configuration/AgentConfigLoader.cs` | TG5 |
| `src/Seeing.Agent/Core/AgentRegistry.cs` | TG5 |

## 修改文件 (12 个)

| 文件 | 改动类型 | 所属 TG |
|------|----------|---------|
| `MCP/McpClientManager.cs` | 删除死代码（-302 行） | TG1 |
| `Extensions/ServiceCollectionExtensions.cs` | DI 异步化 + 新服务注册 | TG2,TG4,TG5 |
| `Configuration/UnifiedConfigManager.cs` | 实现 IAsyncInitializable + ToolPolicies 节 | TG2,TG4 |
| `Configuration/AgentManager.cs` | 异步构造 → 门面（899→120 行） | TG2,TG5 |
| `Configuration/SeeingAgentOptions.cs` | 新增 ToolPolicies 属性 | TG4 |
| `Tools/ToolManager.cs` | 动态装饰器 + 移除 GetAwaiter | TG2,TG4 |
| `Core/Hooks/IHookManager.cs` | 扩展异常事件+指标 | TG3 |
| `Core/Hooks/HookManager.cs` | 完整重写（~120→~200 行） | TG3 |
| `Decorators/TimeoutToolDecorator.cs` | 修复 TimeoutToolContext | TG4 |
| `MCP/Core/IMcpController.cs` | Pause/Resume 异步签名 | TG2 |
| `tests/.../Hooks/HookManagerTests.cs` | 新增 2 测试 | TG3 |
| `samples/.../Pages/ToolsPage.razor` | 策略编辑 UI | TG4 |

## 删除内容

| 位置 | 内容 | 行数 |
|------|------|------|
| `MCP/McpClientManager.cs` | `IMcpClientWrapper` + 2 个 Wrapper 类 | ~302 |
| 全项目 | 所有 `.GetAwaiter().GetResult()` | ~12 处 |

---

# 附录 B: 风险矩阵

| 风险 | 概率 | 影响 | 缓解 |
|------|------|------|------|
| DI 异步化后启动顺序错误 | 中 | 高 | Bootstrapper 按 `InitializationOrder` 严格排序 |
| HookManager 重写导致现有 Hook 失效 | 低 | 高 | 全量 Hook 测试 + 接口向前兼容 |
| 装饰器动态化影响工具执行性能 | 低 | 低 | 每次 `Build` 仅 `new` 三个装饰器，纳秒级 |
| AgentManager → AgentRegistry 破坏调用方 | 中 | 中 | DI 注册同步切换；保留 `IAgentManager` 门面 |
| ToolsPage 策略编辑器保存失败 | 低 | 低 | 现有 `SaveSectionAsync` 已验证 |

---

# 附录 C: 验证清单

完成全部 5 个 Task Group 后执行以下验证：

- [ ] **编译**：`dotnet build Seeing.Agent.slnx -c Debug` 无错误
- [ ] **全量测试**：`dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj -c Debug` 通过率 ≥ 基线
- [ ] **.GetAwaiter().GetResult() 零残留**：
  ```powershell
  Get-ChildItem -Path "src/Seeing.Agent" -Recurse -Filter "*.cs" |
      Select-String "\.GetAwaiter\(\)\.GetResult\(\)" |
      Select-Object Path,LineNumber,Line
  ```
  预期：0 命中
- [ ] **McpClientManager 行数**：`(Get-Content src/Seeing.Agent/MCP/McpClientManager.cs).Length` ≤ 950
- [ ] **AgentManager 行数**：`(Get-Content src/Seeing.Agent/Configuration/AgentManager.cs).Length` ≤ 150
- [ ] **MCP 测试**：`dotnet test ... --filter "FullyQualifiedName~MCP"` 全部通过
- [ ] **Hooks 测试**：`dotnet test ... --filter "FullyQualifiedName~Hooks"` 全部通过
- [ ] **Decorator 测试**：`dotnet test ... --filter "FullyQualifiedName~Decorator"` 全部通过
- [ ] **WebUI 编译**：`dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj -c Debug`
- [ ] **seeing.json 示例**：手工验证配置热更新
  ```json
  {
    "SeeingAgent": {
      "ToolPolicies": {
        "Default": { "TimeoutSeconds": 30, "MaxRetries": 3 },
        "Overrides": {
          "bash": { "TimeoutSeconds": 300, "MaxRetries": 1 }
        }
      }
    }
  }
  ```
