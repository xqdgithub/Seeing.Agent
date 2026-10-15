# 统一重载编排器（ReloadOrchestrator）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立接口级强制的统一重载框架，使工作区切换/配置变更后所有组件（会话/Skill/MCP/插件/Agent/Provider/Model 等）自动重载，无需重启。

**Architecture:** Abstractions 定义 `IReloadSignal`（强类型变更数据）+ `IReloadHandler`（重载处理器接口，`ChangeTypes` 声明式订阅）+ `IReloadSignalBus`/`IReloadHandlerRegistry`（插件推送与动态注册入口）；主库 `ReloadOrchestrator` 单点订阅 `ConfigChanged` + `WorkspaceRootChanged`，按 ChangeTypes 路由分发，串行执行、失败隔离、双触发去抖；所有组件迁移为 Handler 经 DI 显式注册（动态加载插件经 `IReloadHandlerRegistry` 注册）。

**Tech Stack:** .NET 10 / C# / xUnit 2.9 / Moq 4.20 / FluentAssertions 6.12

**设计文档:** `docs/superpowers/specs/2026-08-20-reload-orchestrator-design.md`

**测试命令（各任务通用）:**
```bash
dotnet test tests/Seeing.Agent.Tests            # 主库测试
dotnet test tests/Seeing.Session.Tests          # 原语层测试
dotnet build Seeing.Agent.slnx                  # 全量构建
```

---

## Phase 1：Abstractions 契约层

### Task 1: IReloadSignal 重载信号

**Files:**
- Create: `src/Seeing.Agent.Abstractions/Configuration/ReloadSignal.cs`
- Test: `tests/Seeing.Agent.Tests/Configuration/ReloadSignalTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadSignalTests
{
    [Fact]
    public void ConfigChange_默认空节列表()
    {
        var change = new ConfigChange();
        change.ChangedSections.Should().BeEmpty();
    }

    [Fact]
    public void ConfigChange_携带变更节()
    {
        var change = new ConfigChange { ChangedSections = new[] { "Providers" } };
        change.ChangedSections.Should().Contain("Providers");
    }

    [Fact]
    public void WorkspaceChange_携带新旧工作区()
    {
        var change = new WorkspaceChange { OldWorkspace = "/old", NewWorkspace = "/new" };
        change.OldWorkspace.Should().Be("/old");
        change.NewWorkspace.Should().Be("/new");
    }

    [Fact]
    public void 变更数据实现IReloadSignal接口()
    {
        new ConfigChange().Should().BeAssignableTo<IReloadSignal>();
        new WorkspaceChange().Should().BeAssignableTo<IReloadSignal>();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadSignalTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>重载变更数据基接口：每种触发源一个强类型实现</summary>
public interface IReloadSignal { }

/// <summary>配置节变更数据</summary>
public sealed class ConfigChange : IReloadSignal
{
    /// <summary>发生变更的配置节名称列表（空数组表示全量重载）</summary>
    public IReadOnlyList<string> ChangedSections { get; init; } = Array.Empty<string>();
}

/// <summary>工作区变更数据</summary>
public sealed class WorkspaceChange : IReloadSignal
{
    /// <summary>旧工作区根目录</summary>
    public string? OldWorkspace { get; init; }

    /// <summary>新工作区根目录</summary>
    public string? NewWorkspace { get; init; }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadSignalTests`
Expected: PASS (4)

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Abstractions/Configuration/ReloadSignal.cs tests/Seeing.Agent.Tests/Configuration/ReloadSignalTests.cs
git commit -m "feat(abstractions): 新增 IReloadSignal 强类型重载信号"
```

---

### Task 2: IReloadHandler + ReloadHandlerBase

**Files:**
- Create: `src/Seeing.Agent.Abstractions/Configuration/IReloadHandler.cs`
- Test: `tests/Seeing.Agent.Tests/Configuration/ReloadHandlerTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadHandlerTests
{
    private sealed class TestHandler : ReloadHandlerBase<ConfigChange>
    {
        public override string ComponentId => "test";
        public List<string> Received = new();
        protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
        {
            Received.Add(string.Join(",", change.ChangedSections));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ChangeTypes_声明订阅类型()
    {
        var handler = new TestHandler();
        handler.ChangeTypes.Should().Contain(typeof(ConfigChange));
    }

    [Fact]
    public async Task ReloadAsync_匹配类型分发()
    {
        var handler = new TestHandler();
        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } }, CancellationToken.None);
        handler.Received.Should().Contain("X");
    }

    [Fact]
    public async Task ReloadAsync_不匹配类型忽略()
    {
        var handler = new TestHandler();
        await handler.ReloadAsync(new WorkspaceChange { NewWorkspace = "/new" }, CancellationToken.None);
        handler.Received.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadHandlerTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 组件重载处理器：实现者必须声明订阅的变更类型（编译期可见、可自检）
/// <para>通过 DI 注册（AddSingleton&lt;IReloadHandler, T&gt;），由 ReloadOrchestrator 统一收集调度</para>
/// </summary>
public interface IReloadHandler
{
    /// <summary>组件标识（日志与诊断用）</summary>
    string ComponentId { get; }

    /// <summary>声明订阅的变更类型（支持订阅多个源）</summary>
    IReadOnlyList<Type> ChangeTypes { get; }

    /// <summary>执行重载（由编排器统一调度）</summary>
    Task ReloadAsync(IReloadSignal change, CancellationToken ct = default);
}

/// <summary>
/// 泛型便捷基类：Handler 直接拿到强类型变更数据，无需 pattern match
/// </summary>
public abstract class ReloadHandlerBase<TChange> : IReloadHandler where TChange : IReloadSignal
{
    /// <inheritdoc/>
    public abstract string ComponentId { get; }

    /// <inheritdoc/>
    public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(TChange) };

    /// <inheritdoc/>
    public Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
        => change is TChange typed ? ReloadAsync(typed, ct) : Task.CompletedTask;

    /// <summary>执行强类型重载</summary>
    protected abstract Task ReloadAsync(TChange change, CancellationToken ct);
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadHandlerTests`
Expected: PASS (3)

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Abstractions/Configuration/IReloadHandler.cs tests/Seeing.Agent.Tests/Configuration/ReloadHandlerTests.cs
git commit -m "feat(abstractions): 新增 IReloadHandler 重载处理器接口与泛型基类"
```

---

### Task 2b: IReloadSignalBus + IReloadHandlerRegistry + ReloadResult

插件（扩展包）作为推送方/动态 Handler 注册方的依赖规范入口，全部定义在 Abstractions；`ReloadResult` 从主库移到 Abstractions（推送方需接收结果类型）。

**Files:**
- Create: `src/Seeing.Agent.Abstractions/Configuration/ReloadSignalBus.cs`（含 ReloadResult + IReloadSignalBus + IReloadHandlerRegistry）
- Test: `tests/Seeing.Agent.Tests/Configuration/ReloadSignalBusTests.cs`

- [ ] **Step 1: 写失败测试（契约存在性 + 签名自检）**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadSignalBusTests
{
    private sealed class FakeSignal : IReloadSignal { }
    private sealed class FakeHandler : IReloadHandler
    {
        public string ComponentId => "fake";
        public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(FakeSignal) };
        public Task ReloadAsync(IReloadSignal change, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void PublishAsync_返回结果集合()
    {
        var iface = typeof(IReloadSignalBus);
        var method = iface.GetMethod(nameof(IReloadSignalBus.PublishAsync))!;
        method.ReturnType.Should().Be(typeof(Task<IReadOnlyList<ReloadResult>>));
        method.GetParameters()[0].ParameterType.Should().Be(typeof(IReloadSignal));
    }

    [Fact]
    public void Registry_提供注册与注销()
    {
        typeof(IReloadHandlerRegistry).GetMethod(nameof(IReloadHandlerRegistry.RegisterHandler))!.GetParameters()[0]
            .ParameterType.Should().Be(typeof(IReloadHandler));
        typeof(IReloadHandlerRegistry).GetMethod(nameof(IReloadHandlerRegistry.UnregisterHandler))!.GetParameters()[0]
            .ParameterType.Should().Be(typeof(IReloadHandler));
    }

    [Fact]
    public void ReloadResult_含诊断字段()
    {
        var result = new ReloadResult { ComponentId = "c", Success = true, Error = null, Duration = TimeSpan.FromMilliseconds(5) };
        result.ComponentId.Should().Be("c");
        result.Success.Should().BeTrue();
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(5));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadSignalBusTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>重载执行结果（推送方与编排器共用）</summary>
public sealed class ReloadResult
{
    public string ComponentId { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 重载信号总线：变更推送方（含插件/扩展包）通过此接口发布信号
/// <para>主库 ReloadOrchestrator 实现并注册为该接口；扩展包只引用 Abstractions，依赖规范合规</para>
/// </summary>
public interface IReloadSignalBus
{
    Task<IReadOnlyList<ReloadResult>> PublishAsync(IReloadSignal signal, CancellationToken ct = default);
}

/// <summary>
/// 重载处理器注册表：供运行时动态加载的插件（ExtensionManager 加载的 DLL）
/// 在 InitializeAsync 时注册/注销自己的 Handler；静态 DI 注册仍是推荐路径
/// </summary>
public interface IReloadHandlerRegistry
{
    void RegisterHandler(IReloadHandler handler);
    void UnregisterHandler(IReloadHandler handler);
}
```

> **依赖规范验证**：以上三类型均在 Abstractions，扩展包（Seeing.Agent.Memory/Scheduler/Acp/Gateway 及外部插件）可安全引用；主库 `ReloadOrchestrator` 引用它们实现接口。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadSignalBusTests`
Expected: PASS (3)

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Abstractions/Configuration/ReloadSignalBus.cs tests/Seeing.Agent.Tests/Configuration/ReloadSignalBusTests.cs
git commit -m "feat(abstractions): 新增 IReloadSignalBus/IReloadHandlerRegistry/ReloadResult 契约"
```

---

### Task 3: IComponentLoader 增加 ReloadAsync

**Files:**
- Modify: `src/Seeing.Agent.Abstractions/Components/IComponentManager.cs:27-37`
- Test: `tests/Seeing.Agent.Tests/Configuration/ComponentLoaderContractTests.cs`

- [ ] **Step 1: 写失败测试（反射契约检查，沿用项目现有接口测试风格）**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Components;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ComponentLoaderContractTests
{
    [Fact]
    public void IComponentLoader_声明ReloadAsync()
    {
        var method = typeof(IComponentLoader).GetMethod("ReloadAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<ComponentLoadResult>));
        method.GetParameters().Length.Should().Be(3); // services, workspaceRoot, ct
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ComponentLoaderContractTests`
Expected: FAIL（ReloadAsync 不存在）

- [ ] **Step 3: 实现（默认转调 LoadAsync，避免破坏扩展包）**

在 `IComponentLoader` 接口中追加成员：

```csharp
public interface IComponentLoader
{
    /// <summary>组件类型（字符串标识，如 "Skill" / "Mcp" / "Plugin" / "Rule"）</summary>
    string Type { get; }

    /// <summary>加载组件</summary>
    Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 重载组件（默认转调 <see cref="LoadAsync"/>；需要真正重载语义的加载器必须覆盖）
    /// </summary>
    Task<ComponentLoadResult> ReloadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
        => LoadAsync(services, workspaceRoot, cancellationToken);
}
```

- [ ] **Step 4: 运行确认通过 + 全量构建**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ComponentLoaderContractTests && dotnet build Seeing.Agent.slnx`
Expected: PASS + BUILD SUCCEEDED

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Abstractions/Components/IComponentManager.cs tests/Seeing.Agent.Tests/Configuration/ComponentLoaderContractTests.cs
git commit -m "feat(abstractions): IComponentLoader 增加 ReloadAsync（默认转调 LoadAsync）"
```

---

## Phase 2：Seeing.Session 原语层

### Task 4: IRelocatableSessionStore + FileSessionStore.SetBaseDirectory

**Files:**
- Create: `src/Seeing.Session/Storage/IRelocatableSessionStore.cs`
- Modify: `src/Seeing.Session/Storage/FileSessionStore.cs:15,32-44`
- Test: `tests/Seeing.Session.Tests/FileSessionStoreTests.cs`（若不存在则新建 `RelocatableSessionStoreTests.cs`）

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class RelocatableSessionStoreTests
{
    [Fact]
    public async Task SetBaseDirectory_切换后读写新目录()
    {
        var root1 = Path.Combine(Path.GetTempPath(), "seeing-session-test-" + Guid.NewGuid().ToString("N"));
        var root2 = Path.Combine(Path.GetTempPath(), "seeing-session-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(root1);
            var session = new SessionData { Id = "s1", Title = "旧工作区会话" };
            await store.SaveAsync(session);

            store.SetBaseDirectory(root2);
            await store.SaveAsync(new SessionData { Id = "s2", Title = "新工作区会话" });

            (await store.LoadAsync("s1")).Should().BeNull();   // 旧目录不可见
            (await store.LoadAsync("s2")).Should().NotBeNull(); // 新目录可读写
        }
        finally
        {
            if (Directory.Exists(root1)) Directory.Delete(root1, true);
            if (Directory.Exists(root2)) Directory.Delete(root2, true);
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Session.Tests --filter FullyQualifiedName~RelocatableSessionStoreTests`
Expected: FAIL（SetBaseDirectory 不存在）

- [ ] **Step 3: 实现**

创建 `IRelocatableSessionStore.cs`：

```csharp
namespace Seeing.Session.Storage;

/// <summary>
/// 可重定位的会话存储：支持运行时切换基础目录（用于工作区切换）
/// </summary>
public interface IRelocatableSessionStore : ISessionStore
{
    /// <summary>切换基础目录（切换后读写操作指向新目录）</summary>
    void SetBaseDirectory(string baseDirectory);
}
```

修改 `FileSessionStore.cs`：

```csharp
public class FileSessionStore : ISessionStore, IRelocatableSessionStore, IDisposable
{
    private volatile string _baseDirectory;   // 原 readonly 改为 volatile
    ...
    public FileSessionStore(string? baseDirectory = null, ILogger<FileSessionStore>? logger = null)
    {
        _logger = logger;
        _baseDirectory = baseDirectory ?? GetDefaultSessionDirectory();
        ...
    }

    /// <inheritdoc/>
    public void SetBaseDirectory(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("基础目录不能为空", nameof(baseDirectory));

        _baseDirectory = baseDirectory;
        EnsureDirectoryExists();
        _logger?.LogInformation("会话存储目录已切换: {Directory}", _baseDirectory);
    }
    ...
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Session.Tests --filter FullyQualifiedName~RelocatableSessionStoreTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Session/Storage/IRelocatableSessionStore.cs src/Seeing.Session/Storage/FileSessionStore.cs tests/Seeing.Session.Tests/Storage/
git commit -m "feat(session): 新增 IRelocatableSessionStore 支持运行时切换目录"
```

---

### Task 5: SessionManager.ClearCache

**Files:**
- Modify: `src/Seeing.Session/Management/SessionManager.cs`（缓存区）
- Test: `tests/Seeing.Session.Tests/Management/SessionManagerClearCacheTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Management;

public class SessionManagerClearCacheTests
{
    [Fact]
    public async Task ClearCache_清空内存缓存()
    {
        var store = new InMemorySessionStore();
        var manager = new SessionManager(store: store);

        await manager.SaveAsync(new SessionData { Id = "s1", Title = "旧会话" });
        manager.Get("s1").Should().NotBeNull();

        manager.ClearCache();

        manager.Get("s1").Should().BeNull();
        (await manager.LoadAsync("s1")).Should().BeNull(); // 内存缓存已清，存储无此会话
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Session.Tests --filter FullyQualifiedName~SessionManagerClearCacheTests`
Expected: FAIL（ClearCache 不存在）

- [ ] **Step 3: 实现**

在 `SessionManager` 中添加：

```csharp
/// <summary>
/// 清空内存会话缓存（用于工作区切换等场景，下次访问时从存储重新加载）
/// </summary>
public void ClearCache()
{
    _sessionDataCache.Clear();
    _logger?.LogDebug("会话内存缓存已清空");
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Session.Tests --filter FullyQualifiedName~SessionManagerClearCacheTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Session/Management/SessionManager.cs tests/Seeing.Session.Tests/Management/
git commit -m "feat(session): SessionManager 增加 ClearCache 清空只增缓存"
```

---

## Phase 3：主库编排器

### Task 6: ReloadOrchestrator

**Files:**
- Create: `src/Seeing.Agent/Configuration/ReloadOrchestrator.cs`
- Test: `tests/Seeing.Agent.Tests/Configuration/ReloadOrchestratorTests.cs`

- [ ] **Step 1: 写失败测试（路由/串行/失败隔离/去抖/重入合并/自检）**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadOrchestratorTests
{
    private sealed class TrackingHandler : IReloadHandler
    {
        public string ComponentId => "track";
        public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(ConfigChange) };
        public List<string> Received { get; } = new();
        public Func<ConfigChange, Task>? OnConfig;
        public Task ReloadAsync(IReloadSignal change, CancellationToken ct)
        {
            if (change is ConfigChange cfg)
            {
                lock (Received) Received.Add(string.Join(",", cfg.ChangedSections));
                return OnConfig?.Invoke(cfg) ?? Task.CompletedTask;
            }
            return Task.CompletedTask;
        }
    }

    private static (ReloadOrchestrator orch, TrackingHandler handler) CreateOrchestrator(
        out Mock<IConfigSectionStore> configStore, out Mock<IWorkspaceProvider> workspace)
    {
        configStore = new Mock<IConfigSectionStore>();
        workspace = new Mock<IWorkspaceProvider>();
        var handler = new TrackingHandler();
        var orch = new ReloadOrchestrator(
            new[] { handler }, configStore.Object, workspace.Object,
            NullLogger<ReloadOrchestrator>.Instance);
        return (orch, handler);
    }

    [Fact]
    public async Task ConfigChanged_路由到匹配Handler()
    {
        var (orch, handler) = CreateOrchestrator(out var configStore, out var workspace);
        configStore.Raise(x => x.ConfigChanged += null, new ConfigChangedEventArgs { ChangedSections = new[] { "Providers" } });

        await Task.Delay(200); // 等待异步调度
        handler.Received.Should().Contain("Providers");
    }

    [Fact]
    public async Task WorkspaceChanged_构造WorkspaceChange()
    {
        var (orch, handler) = CreateOrchestrator(out var configStore, out var workspace);
        workspace.Raise(x => x.WorkspaceRootChanged += null, new WorkspaceChangedEventArgs { OldWorkspace = "/old", NewWorkspace = "/new" });

        await Task.Delay(200);
        handler.Received.Should().NotBeEmpty();
    }

    [Fact]
    public async Task 失败隔离_一个Handler异常不影响其他()
    {
        var bad = new TrackingHandler { ComponentId = "bad", OnConfig = _ => throw new InvalidOperationException("boom") };
        var good = new TrackingHandler();
        var orch = new ReloadOrchestrator(
            new IReloadHandler[] { bad, good },
            new Mock<IConfigSectionStore>().Object, new Mock<IWorkspaceProvider>().Object,
            NullLogger<ReloadOrchestrator>.Instance);

        orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } }).GetAwaiter().GetResult();

        good.Received.Should().Contain("X");
    }

    [Fact]
    public async Task 显式ReloadAsync_返回结果集合()
    {
        var (orch, _) = CreateOrchestrator(out _, out _);
        var results = await orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } });
        results.Should().HaveCount(1);
        results[0].ComponentId.Should().Be("track");
        results[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task 去抖_连续全量变更合并为一次()
    {
        var (orch, handler) = CreateOrchestrator(out var configStore, out _);
        var args = new ConfigChangedEventArgs { ChangedSections = Array.Empty<string>() };

        configStore.Raise(x => x.ConfigChanged += null, args);
        configStore.Raise(x => x.ConfigChanged += null, args);
        configStore.Raise(x => x.ConfigChanged += null, args);

        await Task.Delay(500);
        lock (handler.Received) handler.Received.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_即ReloadAsync_插件推送可用()
    {
        var (orch, handler) = CreateOrchestrator(out _, out _);
        var bus = (IReloadSignalBus)orch;
        var results = await bus.PublishAsync(new ConfigChange { ChangedSections = new[] { "P" } });
        results.Should().HaveCount(1);
        handler.Received.Should().Contain("P");
    }

    [Fact]
    public async Task 动态注册_RegisterHandler_后路由生效()
    {
        var (orch, _) = CreateOrchestrator(out _, out _);
        var registry = (IReloadHandlerRegistry)orch;
        var pluginHandler = new TrackingHandler { ComponentId = "plugin" };
        registry.RegisterHandler(pluginHandler);

        var results = await orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } });
        results.Select(r => r.ComponentId).Should().Contain("plugin");
        pluginHandler.Received.Should().Contain("X");

        registry.UnregisterHandler(pluginHandler);
        results = await orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Y" } });
        results.Select(r => r.ComponentId).Should().NotContain("plugin");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadOrchestratorTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 统一重载编排器：订阅配置变更与工作区变更，按 IReloadHandler.ChangeTypes 路由分发
/// <para>串行执行、失败隔离、全量变更去抖合并、执行期间重入合并为一次待处理</para>
/// <para>实现 IReloadSignalBus（插件推送）与 IReloadHandlerRegistry（插件动态注册）</para>
/// </summary>
public sealed class ReloadOrchestrator : IReloadSignalBus, IReloadHandlerRegistry, IDisposable
{
    /// <summary>全量变更去抖窗口</summary>
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(150);

    private readonly ILogger<ReloadOrchestrator> _logger;
    private readonly IConfigSectionStore _configStore;
    private readonly IWorkspaceProvider _workspace;
    private readonly Dictionary<Type, List<IReloadHandler>> _routes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private bool _pendingFullReload;
    private bool _running;
    private bool _disposed;

    public ReloadOrchestrator(
        IEnumerable<IReloadHandler> handlers,
        IConfigSectionStore configStore,
        IWorkspaceProvider workspace,
        ILogger<ReloadOrchestrator> logger)
    {
        _logger = logger;
        _configStore = configStore;
        _workspace = workspace;

        _routes = new Dictionary<Type, List<IReloadHandler>>();
        foreach (var handler in handlers)
            AddToRoutes(handler);

        _configStore.ConfigChanged += OnConfigChanged;
        _workspace.WorkspaceRootChanged += OnWorkspaceChanged;

        LogRegistrationSummary();
    }

    /// <summary>显式触发重载（供手动调用，如未来文件监视/CLI 命令；IReloadSignalBus.PublishAsync 即此方法）</summary>
    public async Task<IReadOnlyList<ReloadResult>> ReloadAsync(
        IReloadSignal change, CancellationToken ct = default)
    {
        var results = new List<ReloadResult>();

        if (!_routes.TryGetValue(change.GetType(), out var handlers) || handlers.Count == 0)
        {
            _logger.LogWarning("无组件响应变更类型: {ChangeType}", change.GetType().Name);
            return results;
        }

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var handler in handlers)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await handler.ReloadAsync(change, ct);
                    sw.Stop();
                    results.Add(new ReloadResult { ComponentId = handler.ComponentId, Success = true, Duration = sw.Elapsed });
                    _logger.LogDebug("重载完成: {ComponentId} ({Duration}ms)", handler.ComponentId, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    results.Add(new ReloadResult { ComponentId = handler.ComponentId, Success = false, Error = ex.Message, Duration = sw.Elapsed });
                    _logger.LogError(ex, "组件重载失败: {ComponentId}", handler.ComponentId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReloadResult>> PublishAsync(IReloadSignal signal, CancellationToken ct = default)
        => ReloadAsync(signal, ct);

    /// <inheritdoc/>
    public void RegisterHandler(IReloadHandler handler)
    {
        lock (_stateLock)
        {
            AddToRoutes(handler);
            _logger.LogInformation("动态注册重载处理器: {ComponentId}", handler.ComponentId);
        }
    }

    /// <inheritdoc/>
    public void UnregisterHandler(IReloadHandler handler)
    {
        lock (_stateLock)
        {
            foreach (var changeType in handler.ChangeTypes)
            {
                if (_routes.TryGetValue(changeType, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0) _routes.Remove(changeType);
                }
            }
            _logger.LogInformation("注销重载处理器: {ComponentId}", handler.ComponentId);
        }
    }

    private void AddToRoutes(IReloadHandler handler)
    {
        foreach (var changeType in handler.ChangeTypes)
        {
            if (!_routes.TryGetValue(changeType, out var list))
                _routes[changeType] = list = new List<IReloadHandler>();
            if (!list.Contains(handler)) list.Add(handler);
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        var isFull = e.ChangedSections.Length == 0;
        if (isFull)
        {
            EnqueueFullReload();
            return;
        }
        _ = ReloadAsync(new ConfigChange { ChangedSections = e.ChangedSections });
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        EnqueueFullReload();
    }

    /// <summary>全量变更去抖 + 执行期间重入合并</summary>
    private void EnqueueFullReload()
    {
        lock (_stateLock)
        {
            if (_running)
            {
                _pendingFullReload = true;   // 执行期间重入：合并为一次待处理
                return;
            }
        }

        _ = DebouncedFullReloadAsync();
    }

    private async Task DebouncedFullReloadAsync()
    {
        while (true)
        {
            lock (_stateLock) _running = true;

            try
            {
                await Task.Delay(DebounceWindow);   // 去抖窗口：合并连续全量触发
                await ReloadAsync(new WorkspaceChange
                {
                    OldWorkspace = _workspace.StartupDirectory,
                    NewWorkspace = _workspace.WorkspaceRoot
                });

                // 工作区切换场景：配置随工作区变更，需再执行一轮配置全量重载
                await ReloadAsync(new ConfigChange { ChangedSections = Array.Empty<string>() });
            }
            finally
            {
                lock (_stateLock) _running = false;
            }

            lock (_stateLock)
            {
                if (!_pendingFullReload) break;
                _pendingFullReload = false;
            }
        }
    }

    private void LogRegistrationSummary()
    {
        foreach (var (changeType, handlers) in _routes)
        {
            _logger.LogInformation("重载注册: {ChangeType} <- {Handlers}",
                changeType.Name, string.Join(", ", handlers.Select(h => h.ComponentId)));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configStore.ConfigChanged -= OnConfigChanged;
        _workspace.WorkspaceRootChanged -= OnWorkspaceChanged;
        _gate.Dispose();
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ReloadOrchestratorTests`
Expected: PASS (7)

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Configuration/ReloadOrchestrator.cs tests/Seeing.Agent.Tests/Configuration/ReloadOrchestratorTests.cs
git commit -m "feat(agent): 新增 ReloadOrchestrator 统一重载编排器"
```

---

### Task 7: 主库 DI 注册编排器

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（AddSeeingAgent 末尾，约 590-620 行附近）

- [ ] **Step 1: 注册编排器与空 Handler 集合（Phase 4 各任务再注册具体 Handler）**

在 `AddSeeingAgent` 中工作区 Provider 注册之后添加：

```csharp
// 统一重载编排器（订阅配置/工作区变更，调度所有 IReloadHandler）
services.AddSingleton<ReloadOrchestrator>();
// 插件推送与动态注册入口：均指向编排器，扩展包只引用 Abstractions 接口
services.AddSingleton<IReloadSignalBus>(sp => sp.GetRequiredService<ReloadOrchestrator>());
services.AddSingleton<IReloadHandlerRegistry>(sp => sp.GetRequiredService<ReloadOrchestrator>());
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build Seeing.Agent.slnx`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(agent): DI 注册 ReloadOrchestrator 与推送/动态注册接口"
```

---

## Phase 4：现有订阅者迁移为 Handler

> 迁移模式统一为：删除 `_configManager.ConfigChanged += OnConfigChanged` 自订阅（及 `-=`），新增 `XxxReloadHandler : ReloadHandlerBase<ConfigChange>`，在各自 AddXxx 扩展中注册 `services.AddSingleton<IReloadHandler, XxxReloadHandler>()`。

### Task 8: ProviderManager 迁移

**Files:**
- Modify: `src/Seeing.Agent/Llm/ProviderManager.cs`（删除 39 行订阅、402 行退订、212-222 行 OnConfigChanged）
- Create: `src/Seeing.Agent/Llm/ProviderReloadHandler.cs`
- Test: `tests/Seeing.Agent.Tests/Llm/ProviderManagerTests.cs`（改：通过 Handler 触发）

- [ ] **Step 1: 写失败测试（Handler 触发 Refresh）**

```csharp
[Fact]
public async Task ProviderReloadHandler_配置变更触发刷新()
{
    // Arrange: 构造含 Providers 配置的 manager（复用现有测试基建）
    // Act: 调 handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Providers" } })
    // Assert: manager 中配置驱动 Provider 已注册/重建
}
```

> 具体断言沿用 `ProviderManagerTests` 现有"配置变更重建 Provider"测试逻辑，仅把触发方式从"Raise ConfigChanged"改为"调用 `ProviderReloadHandler.ReloadAsync`"。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ProviderManagerTests`
Expected: FAIL（handler 类型不存在）

- [ ] **Step 3: 实现**

```csharp
// ProviderReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>Provider 配置变更重载处理器</summary>
public sealed class ProviderReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly ProviderManager _manager;

    public ProviderReloadHandler(ProviderManager manager) => _manager = manager;

    public override string ComponentId => "provider";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Length == 0 || change.ChangedSections.Contains("Providers"))
            _manager.RefreshConfiguredProviders();
        return Task.CompletedTask;
    }
}
```

从 `ProviderManager` 删除：`_configManager.ConfigChanged += OnConfigChanged;`、`_configManager.ConfigChanged -= OnConfigChanged;`（402 行）、`OnConfigChanged` 方法（212-222 行）。

- [ ] **Step 4: 运行确认通过 + 全量测试**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ProviderManager" && dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ProviderReloadHandler"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Llm/ProviderManager.cs src/Seeing.Agent/Llm/ProviderReloadHandler.cs tests/Seeing.Agent.Tests/Llm/
git commit -m "refactor(agent): ProviderManager 迁移为 ReloadHandler"
```

---

### Task 9: ModelConfigManager 迁移

**Files:**
- Modify: `src/Seeing.Agent/Llm/ModelConfigManager.cs`（删除 49 行订阅、603 行退订、267-274 行 OnConfigChanged）
- Create: `src/Seeing.Agent/Llm/ModelReloadHandler.cs`
- Test: `tests/Seeing.Agent.Tests/Llm/ModelConfigManagerCharacterizationTests.cs`（改触发方式）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ModelReloadHandler_配置变更入队刷新()
{
    // Arrange: 构造 manager（复用现有基建）
    // Act: 调 handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Providers" } })
    // Assert: 队列收到刷新请求（可通过模型目录变化或内部计数器验证）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelConfigManagerCharacterizationTests"`
Expected: FAIL（handler 不存在）

- [ ] **Step 3: 实现**

```csharp
// ModelReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>模型目录配置变更重载处理器</summary>
public sealed class ModelReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly ModelConfigManager _manager;

    public ModelReloadHandler(ModelConfigManager manager) => _manager = manager;

    public override string ComponentId => "model-catalog";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Length == 0 || change.ChangedSections.Contains("Providers"))
            _manager.EnqueueRefresh("configuration");
        return Task.CompletedTask;
    }
}
```

> `EnqueueRefresh` 已是 internal（ModelConfigManager.cs:279），Handler 与 Manager 同程序集可访问。

从 `ModelConfigManager` 删除订阅与 `OnConfigChanged`（267-274 行）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelConfigManagerCharacterizationTests" && dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelReloadHandler"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Llm/ModelConfigManager.cs src/Seeing.Agent/Llm/ModelReloadHandler.cs tests/Seeing.Agent.Tests/Llm/
git commit -m "refactor(agent): ModelConfigManager 迁移为 ReloadHandler"
```

---

### Task 10: AgentRuntimeManager 迁移 + 全量事件修复

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentRuntimeManager.cs`（79 行订阅、106-125 行 OnConfigChanged）
- Create: `src/Seeing.Agent/Core/AgentRuntimeReloadHandler.cs`
- Test: `tests/Seeing.Agent.Tests/Core/AgentRuntimeReloadHandlerTests.cs`（新增全量事件回归测试）

- [ ] **Step 1: 写失败测试（修复：全量重载事件不再被忽略）**

```csharp
[Fact]
public async Task 全量重载_重新应用AgentModels()
{
    // Arrange: options.AgentModels = { "agent1" = "modelA" }
    // Act: handler.ReloadAsync(new ConfigChange { ChangedSections = Array.Empty<string>() })
    // Assert: agent1 的运行时模型 = modelA（旧行为：ChangedSections 为空直接 return，不处理）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~AgentRuntimeReloadHandlerTests`
Expected: FAIL（handler 不存在 / 修复未生效）

- [ ] **Step 3: 实现**

```csharp
// AgentRuntimeReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Core;

/// <summary>Agent 运行时配置重载处理器：DefaultAgent/AgentModels 变更 + 全量重载</summary>
public sealed class AgentRuntimeReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly AgentRuntimeManager _manager;

    public AgentRuntimeReloadHandler(AgentRuntimeManager manager) => _manager = manager;

    public override string ComponentId => "agent-runtime";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        // 全量重载（ChangedSections 为空）或涉及 AgentModels 时重新应用模型绑定
        if (change.ChangedSections.Length == 0 || change.ChangedSections.Contains("AgentModels"))
        {
            _ = _manager.ApplyAgentModelsAsync();
        }
        if (change.ChangedSections.Length > 0 && change.ChangedSections.Contains("DefaultAgent"))
        {
            // 默认 Agent 变更（原日志行为保留）
        }
        return Task.CompletedTask;
    }
}
```

> `ApplyAgentModelsAsync` 需从 private 改为 internal（AgentRuntimeManager.cs:127），Handler 与 Manager 同程序集。

从 `AgentRuntimeManager` 删除：79 行订阅、`OnConfigChanged`（106-125 行）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~AgentRuntimeReloadHandlerTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Core/AgentRuntimeManager.cs src/Seeing.Agent/Core/AgentRuntimeReloadHandler.cs tests/Seeing.Agent.Tests/Core/
git commit -m "refactor(agent): AgentRuntimeManager 迁移为 Handler 并修复全量事件忽略"
```

---

### Task 11: SchedulerOptionsProvider 迁移

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Configuration/SchedulerOptionsProvider.cs`（35 行订阅、56-62 行 OnConfigChanged）
- Create: `src/Seeing.Agent.Scheduler/Configuration/SchedulerReloadHandler.cs`
- Modify: `src/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs`（注册 Handler）
- Test: `tests/Seeing.Agent.Scheduler.Tests/`（改触发方式）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task SchedulerReloadHandler_配置变更重载()
{
    // Act: handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Scheduler" } })
    // Assert: provider.Current 反映新配置
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Scheduler.Tests`
Expected: FAIL（handler 不存在）

- [ ] **Step 3: 实现**

```csharp
// SchedulerReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>Scheduler 配置变更重载处理器</summary>
public sealed class SchedulerReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly SchedulerOptionsProvider _provider;

    public SchedulerReloadHandler(SchedulerOptionsProvider provider) => _provider = provider;

    public override string ComponentId => "scheduler";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Length == 0 || change.ChangedSections.Contains("Scheduler"))
            _provider.Reload();
        return Task.CompletedTask;
    }
}
```

`SchedulerOptionsProvider` 删除订阅与 OnConfigChanged；`SchedulerServiceCollectionExtensions` 中注册：

```csharp
services.AddSingleton<IReloadHandler, SchedulerReloadHandler>();
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Scheduler.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Scheduler/Configuration/ src/Seeing.Agent.Scheduler/Extensions/ tests/Seeing.Agent.Scheduler.Tests/
git commit -m "refactor(scheduler): SchedulerOptionsProvider 迁移为 ReloadHandler"
```

---

### Task 12: MemoryOptionsProvider 迁移

**Files:**
- Modify: `src/Seeing.Agent.Memory/Configuration/MemoryOptionsProvider.cs`（27 行订阅、57-61 行 OnConfigChanged、65 行退订）
- Create: `src/Seeing.Agent.Memory/Configuration/MemoryReloadHandler.cs`
- Modify: `src/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs`（注册 Handler）
- Test: `tests/Seeing.Agent.Memory.Tests/`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task MemoryReloadHandler_配置变更重载()
{
    // Act: handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Memory" } })
    // Assert: provider.CurrentValue 反映新配置
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Memory.Tests`
Expected: FAIL

- [ ] **Step 3: 实现**

```csharp
// MemoryReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Memory.Configuration;

/// <summary>Memory 配置变更重载处理器</summary>
public sealed class MemoryReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly MemoryOptionsProvider _provider;

    public MemoryReloadHandler(MemoryOptionsProvider provider) => _provider = provider;

    public override string ComponentId => "memory";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Length == 0 || change.ChangedSections.Contains("Memory"))
            _provider.Reload();
        return Task.CompletedTask;
    }
}
```

`MemoryOptionsProvider` 删除订阅与 OnConfigChanged（保留 Reload() 与显式 SaveAsync 后 Reload 的即时路径）；扩展方法中注册 `services.AddSingleton<IReloadHandler, MemoryReloadHandler>()`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Memory.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Memory/Configuration/ src/Seeing.Agent.Memory/Extensions/ tests/Seeing.Agent.Memory.Tests/
git commit -m "refactor(memory): MemoryOptionsProvider 迁移为 ReloadHandler"
```

---

### Task 13: ChannelHostHostedService 迁移

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Channels/ChannelHostHostedService.cs`（43 行订阅、67 行退订、70-76 行 OnConfigChanged）
- Create: `src/Seeing.Agent.Gateway/Channels/ChannelHostReloadHandler.cs`
- Modify: `src/Seeing.Agent.Gateway/Extensions/GatewayServiceCollectionExtensions.cs`（注册 Handler）
- Test: `tests/Seeing.Gateway.Tests/`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ChannelHostReloadHandler_配置变更协调()
{
    // Act: handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "GatewayClients" } })
    // Assert: manager.StartAsync/StopAsync 按启用状态被调用（mock 验证）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Gateway.Tests`
Expected: FAIL

- [ ] **Step 3: 实现**

```csharp
// ChannelHostReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>Gateway/ChannelHost 配置变更重载处理器</summary>
public sealed class ChannelHostReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly ChannelHostHostedService _service;

    public ChannelHostReloadHandler(ChannelHostHostedService service) => _service = service;

    public override string ComponentId => "channel-host";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Length == 0 ||
            change.ChangedSections.Contains("GatewayClients") ||
            change.ChangedSections.Contains("Gateway"))
        {
            _service.ReconcileAsync();   // 需从 private 改 internal
        }
        return Task.CompletedTask;
    }
}
```

`ChannelHostHostedService` 删除订阅与 OnConfigChanged；`ReconcileAsync` private → internal；注册 `services.AddSingleton<IReloadHandler, ChannelHostReloadHandler>()`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Gateway.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Gateway/Channels/ src/Seeing.Agent.Gateway/Extensions/ tests/Seeing.Gateway.Tests/
git commit -m "refactor(gateway): ChannelHostHostedService 迁移为 ReloadHandler"
```

---

### Task 14: AcpReloadHandler（挂接孤儿接口）

**Files:**
- Create: `src/Seeing.Agent.Acp/Hosting/AcpReloadHandler.cs`
- Modify: `src/Seeing.Agent.Acp/Extensions/AcpServiceCollectionExtensions.cs`（注册）
- Test: `tests/Seeing.Agent.Acp.Tests/`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task AcpReloadHandler_配置变更触发重注册()
{
    // Act: handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Acp" } })
    // Assert: IAcpConfigurationReloader.ReloadAsync 被调用（mock 验证）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Acp.Tests`
Expected: FAIL

- [ ] **Step 3: 实现**

```csharp
// AcpReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Acp.Hosting;

/// <summary>ACP 配置变更重载处理器（挂接 IAcpConfigurationReloader）</summary>
public sealed class AcpReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly IAcpConfigurationReloader _reloader;

    public AcpReloadHandler(IAcpConfigurationReloader reloader) => _reloader = reloader;

    public override string ComponentId => "acp";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Length == 0 || change.ChangedSections.Contains("Acp"))
            return _reloader.ReloadAsync(ct);
        return Task.CompletedTask;
    }
}
```

`AcpServiceCollectionExtensions.cs` 中注册：`services.AddSingleton<IReloadHandler, AcpReloadHandler>();`

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Acp.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent.Acp/Hosting/AcpReloadHandler.cs src/Seeing.Agent.Acp/Extensions/ tests/Seeing.Agent.Acp.Tests/
git commit -m "feat(acp): AcpReloadHandler 挂接 IAcpConfigurationReloader"
```

---

### Task 15: AgentManager 工作区重载 Handler

**Files:**
- Create: `src/Seeing.Agent/Configuration/AgentManagerReloadHandler.cs`
- Modify: `src/Seeing.Agent/Configuration/AgentManager.cs`（103 行 ApplyMdOverridesAsync private → internal）
- Test: `tests/Seeing.Agent.Tests/Configuration/AgentManagerReloadHandlerTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task 工作区切换_重新应用MD覆盖()
{
    // Arrange: 构造 AgentManager + 两个工作区目录，各放不同 AGENT.md
    // Act: handler.ReloadAsync(new WorkspaceChange { OldWorkspace="/a", NewWorkspace="/b" })
    // Assert: agentStore 中代理来自新工作区 MD
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~AgentManagerReloadHandlerTests`
Expected: FAIL

- [ ] **Step 3: 实现**

```csharp
// AgentManagerReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Configuration;

/// <summary>工作区切换重载处理器：重新发现并应用 Agent MD 配置</summary>
public sealed class AgentManagerReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly AgentManager _manager;

    public AgentManagerReloadHandler(AgentManager manager) => _manager = manager;

    public override string ComponentId => "agent-md";

    protected override Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
        => _manager.ReloadMdOverridesAsync(ct);
}
```

在 `AgentManager` 中新增公开方法（原 ApplyMdOverridesAsync 逻辑抽取）：

```csharp
/// <summary>重新发现并应用 Agent MD 配置（工作区切换后调用）</summary>
public async Task ReloadMdOverridesAsync(CancellationToken ct = default)
{
    await ApplyMdOverridesAsync();
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~AgentManagerReloadHandlerTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Configuration/AgentManager.cs src/Seeing.Agent/Configuration/AgentManagerReloadHandler.cs tests/Seeing.Agent.Tests/Configuration/
git commit -m "feat(agent): AgentManager 工作区切换重载 Handler"
```

---

### Task 16: SessionReloadHandler（会话存储切换 + 缓存清理）

**Files:**
- Create: `src/Seeing.Agent/Configuration/SessionReloadHandler.cs`
- Test: `tests/Seeing.Agent.Tests/Configuration/SessionReloadHandlerTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task 工作区切换_会话存储换目录并清缓存()
{
    var storeMock = new Mock<IRelocatableSessionStore>();
    var sessionManagerMock = new Mock<SessionManager>(null, null, null, null, null);
    var handler = new SessionReloadHandler(storeMock.Object, sessionManagerMock.Object);

    await handler.ReloadAsync(new WorkspaceChange { OldWorkspace = "/old", NewWorkspace = "/new" }, CancellationToken.None);

    storeMock.Verify(x => x.SetBaseDirectory(Path.Combine("/new", ".seeing", "sessions")), Times.Once);
    sessionManagerMock.Verify(x => x.ClearCache(), Times.Once);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~SessionReloadHandlerTests`
Expected: FAIL

- [ ] **Step 3: 实现**

```csharp
// SessionReloadHandler.cs
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 工作区切换重载处理器：会话存储切换到新工作区目录并清空内存缓存
/// </summary>
public sealed class SessionReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly ISessionStore _store;
    private readonly SessionManager _sessionManager;
    private readonly IWorkspaceProvider _workspace;

    public SessionReloadHandler(
        ISessionStore store,
        SessionManager sessionManager,
        IWorkspaceProvider workspace)
    {
        _store = store;
        _sessionManager = sessionManager;
        _workspace = workspace;
    }

    public override string ComponentId => "session";

    protected override Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
    {
        if (_store is IRelocatableSessionStore relocatable)
        {
            relocatable.SetBaseDirectory(Path.Combine(_workspace.ProjectSeeingDirectory, "sessions"));
        }
        else
        {
            // 存储不支持重定位：清缓存避免旧会话残留
            _sessionManager.ClearCache();
        }
        _sessionManager.ClearCache();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~SessionReloadHandlerTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Configuration/SessionReloadHandler.cs tests/Seeing.Agent.Tests/Configuration/SessionReloadHandlerTests.cs
git commit -m "feat(agent): SessionReloadHandler 工作区切换换目录+清缓存"
```

---

### Task 17: 主库 Handler 注册收尾

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 注册 Task 8/9/10/15/16 的 Handler**

在 `AddSeeingAgent` 中编排器注册附近添加：

```csharp
// 统一重载 Handler 注册（组件迁移自 ConfigChanged 自订阅）
services.AddSingleton<IReloadHandler, ProviderReloadHandler>();
services.AddSingleton<IReloadHandler, ModelReloadHandler>();
services.AddSingleton<IReloadHandler, AgentRuntimeReloadHandler>();
services.AddSingleton<IReloadHandler, AgentManagerReloadHandler>();
services.AddSingleton<IReloadHandler, SessionReloadHandler>();
```

> 注意：`IReloadHandler` 的解析依赖各 Manager 已注册（它们均在 AddSeeingAgent 中注册），Handler 注册必须放在对应 Manager 注册之后。

- [ ] **Step 2: 构建 + 全量测试**

Run: `dotnet build Seeing.Agent.slnx && dotnet test tests/Seeing.Agent.Tests`
Expected: BUILD SUCCEEDED + 全部 PASS

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(agent): 主库注册全部重载 Handler"
```

---

## Phase 5：组件加载器重载

### Task 18: McpClientManager 幂等初始化 + ResetAllAsync

**Files:**
- Modify: `src/Seeing.Agent/MCP/McpClientManager.cs`（709-748 行 InitializeAsync）
- Test: `tests/Seeing.Agent.Tests/MCP/McpClientManagerReinitializeTests.cs`

- [ ] **Step 1: 写失败测试（重复初始化不重复注册工具）**

```csharp
[Fact]
public async Task InitializeAsync_重复调用不重复注册工具()
{
    // Arrange: mock ToolManager + 构造 manager，首次 InitializeAsync(配置A)
    // Act: 第二次 InitializeAsync(配置A)
    // Assert: 工具注册次数 = 配置中的工具数（不翻倍）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~McpClientManagerReinitializeTests`
Expected: FAIL（当前 CompareExchange 直接 return 时通过但无法验证重载场景；用 ResetAll 场景验证失败）

- [ ] **Step 3: 实现**

修改 `McpClientManager.InitializeAsync`：

```csharp
public async Task InitializeAsync(IReadOnlyDictionary<string, McpServerConfig> configs, CancellationToken cancellationToken = default)
{
    // 已初始化时先重置，支持工作区切换后重新初始化
    if (Interlocked.CompareExchange(ref _initialized, true, false))
    {
        await ResetAllAsync(cancellationToken);
    }
    ... // 原初始化主体不变
}

/// <summary>
/// 重置全部 MCP 状态并断开所有连接（供重新初始化前调用）
/// </summary>
public async Task ResetAllAsync(CancellationToken cancellationToken = default)
{
    _reconnector.Stop();

    foreach (var name in _coordinators.Keys.ToList())
    {
        try
        {
            if (_coordinators.TryGetValue(name, out var coordinator))
                await coordinator.DisconnectAsync(cancellationToken);
            await _toolRegistry.UnregisterAllToolsAsync(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重置 MCP Server {Name} 失败", name);
        }
    }

    lock (_stateLock)
    {
        _coordinators.Clear();
        _configs.Clear();
        _statuses.Clear();
    }

    _processMonitor.Stop();
    _reconnector.Start(_shutdownCts.Token);
    _logger.LogInformation("MCP 状态已重置");
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~McpClientManagerReinitializeTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/MCP/McpClientManager.cs tests/Seeing.Agent.Tests/MCP/
git commit -m "fix(mcp): InitializeAsync 支持重复初始化，新增 ResetAllAsync"
```

---

### Task 19: ComponentManager 三个 Loader 实现 ReloadAsync

**Files:**
- Modify: `src/Seeing.Agent/Core/ComponentManager.cs`（SkillLoader/McpLoader/PluginLoader + ComponentManager 实现 IReloadHandler）
- Test: `tests/Seeing.Agent.Tests/Core/ComponentManagerReloadTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ComponentManager_工作区切换触发三Loader重载()
{
    // Arrange: mock SkillManager/McpClientManager/ExtensionManager + 构造 ComponentManager
    // Act: componentManager.ReloadAsync(new WorkspaceChange {...})
    // Assert: SkillManager.DiscoverSkillsAsync 被调用；McpLoader 重新加载；ExtensionManager 重载
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ComponentManagerReloadTests`
Expected: FAIL

- [ ] **Step 3: 实现**

**SkillLoader 增加 ReloadAsync：**

```csharp
public async Task<ComponentLoadResult> ReloadAsync(
    IServiceProvider services, string workspaceRoot, CancellationToken cancellationToken = default)
{
    // 重挂载目录 + 重新发现（复用 LoadAsync 主体，但先清空旧技能）
    var skillManager = services.GetRequiredService<SkillManager>();
    skillManager.ClearSkillInfos();
    return await LoadAsync(services, workspaceRoot, cancellationToken);
}
```

**McpLoader 增加 ReloadAsync：**

```csharp
public async Task<ComponentLoadResult> ReloadAsync(
    IServiceProvider services, string workspaceRoot, CancellationToken cancellationToken = default)
{
    var mcpManager = services.GetRequiredService<McpClientManager>();
    var workspaceProvider = services.GetService<IWorkspaceProvider>() ?? new WorkspaceProvider(workspaceRoot);
    var configs = McpConfigLoader.LoadDefault(workspaceProvider, logger);

    // 重置全部连接后重新初始化（幂等）
    await mcpManager.ResetAllAsync(cancellationToken);
    await mcpManager.InitializeAsync(
        configs.ToDictionary(c => c.Name, c => c), cancellationToken);

    return new ComponentLoadResult { Type = Type, Success = true, Count = configs.Count };
}
```

**PluginLoader 增加 ReloadAsync：**

```csharp
public async Task<ComponentLoadResult> ReloadAsync(
    IServiceProvider services, string workspaceRoot, CancellationToken cancellationToken = default)
{
    var extensionManager = services.GetRequiredService<ExtensionManager>();
    await extensionManager.DisposeAllAsync();
    return await LoadAsync(services, workspaceRoot, cancellationToken);
}
```

> 前置检查：`ExtensionManager.DisposeAllAsync`（369 行）已存在；`InitializeAsync` 需支持重复调用（若 LoadExternal 有残留状态需清理，在该方法内补充幂等处理）。

**ComponentManager 实现 IReloadHandler：**

```csharp
public class ComponentManager : IComponentManager, IReloadHandler
{
    public string ComponentId => "components";

    public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(WorkspaceChange), typeof(ConfigChange) };

    public async Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
    {
        var workspaceRoot = _services.GetRequiredService<IWorkspaceProvider>().WorkspaceRoot;

        if (change is WorkspaceChange)
        {
            await LoadAllAsync(workspaceRoot, ct);   // 全量重载
        }
        else if (change is ConfigChange cfg)
        {
            if (cfg.ChangedSections.Length == 0)
            {
                await LoadAllAsync(workspaceRoot, ct);
                return;
            }
            foreach (var section in cfg.ChangedSections)
            {
                if (section == "Skills") await LoadAsync("Skill", workspaceRoot, ct);
                else if (section == "Mcp") await LoadAsync("Mcp", workspaceRoot, ct);
                else if (section is "Plugins" or "PluginEnabled") await LoadAsync("Plugin", workspaceRoot, ct);
            }
        }
    }
    ...
}
```

> `LoadAsync("Skill", ...)` 现有签名是 `LoadAsync(string type, ...)`，Loader 的 ReloadAsync 需在 `ComponentManager.LoadAsync` 内部按类型分发（见 ComponentManager.cs:109-149）：`LoadAsync(type)` 增加分支——当 `_loadStatus[type].Success` 为 true 时调用 loader.ReloadAsync，否则调用 LoadAsync。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter FullyQualifiedName~ComponentManagerReloadTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Core/ComponentManager.cs tests/Seeing.Agent.Tests/Core/
git commit -m "feat(agent): 三个 Loader 实现 ReloadAsync，ComponentManager 实现 IReloadHandler"
```

---

### Task 20: 注册 ComponentManager Handler

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 注册**

在 Task 17 注册列表后追加：

```csharp
services.AddSingleton<IReloadHandler>(sp => sp.GetRequiredService<ComponentManager>());
```

- [ ] **Step 2: 构建 + 全量测试**

Run: `dotnet build Seeing.Agent.slnx && dotnet test tests/Seeing.Agent.Tests`
Expected: BUILD SUCCEEDED + 全部 PASS

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(agent): 注册 ComponentManager 重载 Handler"
```

---

## Phase 6：WebUI 集成

### Task 21: WorkspaceSwitchService 简化

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/WorkspaceSwitchService.cs:66-75,106-113`
- Modify: `tests/Seeing.Agent.WebUI.Tests/Services/WorkspaceSwitchServiceTests.cs`

- [ ] **Step 1: 更新测试（切换不再手动加载状态）**

```csharp
[Fact]
public async Task SwitchWorkspaceAsync_不再手动加载ToolSkill状态()
{
    // Act: await service.SwitchWorkspaceAsync(newPath)
    // Assert: 仅 SetWorkspaceRoot + ReloadAsync 被调用（工具/技能状态由编排器 Handler 处理）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Seeing.Agent.WebUI.Tests`
Expected: FAIL（行为不符）

- [ ] **Step 3: 实现**

`SwitchWorkspaceAsync` 中删除手动状态加载（第 72-74 行）：

```csharp
// 1. 更新工作区根目录（触发 WorkspaceRootChanged → 编排器全量重载）
_workspace.SetWorkspaceRoot(newWorkspaceRoot);

// 2. 重新加载配置（编排器对全量变更去抖合并，此处仅刷新配置快照）
await _configService.ReloadAsync(cancellationToken);

_logger.LogInformation("工作区切换完成: {Path}", newWorkspaceRoot);
```

`SetWorkspaceOptionsAsync` 同样删除手动状态加载（112-113 行）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Seeing.Agent.WebUI.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/WorkspaceSwitchService.cs tests/Seeing.Agent.WebUI.Tests/
git commit -m "refactor(webui): 工作区切换依赖编排器重载，移除手动状态加载"
```

---

### Task 22: 全量验证

- [ ] **Step 1: 全量构建 + 全量测试**

```bash
dotnet build Seeing.Agent.slnx
dotnet test Seeing.Agent.slnx
```

Expected: 全部成功

- [ ] **Step 2: 手动冒烟（可选）**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
# 1. 在 UI 切换工作区 → 会话列表应显示新工作区会话
# 2. 编辑新工作区 .seeing/seeing.json 的 Providers 节保存 → 模型列表热更新
# 3. 新工作区放 SKILL.md → 技能列表出现（无需重启）
```

- [ ] **Step 3: 提交**

```bash
git add -A
git commit -m "chore: 重载框架全量验证"
```

---

## 自审备注

- **范围覆盖**：设计文档第 3 节（接口：IReloadSignal/IReloadHandler/IReloadSignalBus/IReloadHandlerRegistry/ReloadResult/IComponentLoader）→ Task 1-3（含 2b）；第 4 节（编排器）→ Task 6-7；第 5 节（组件清单 1-15）→ Task 8-20；第 6 节（原语层）→ Task 4-5；第 7-8 节（触发流/错误处理）→ 编排器实现内置；第 9 节（测试）→ 各任务内联；第 10 节（兼容性）→ Task 3 默认实现 + 各 Handler 幂等。
- **类型一致性**：`ReloadHandlerBase<TChange>`、`ChangeTypes`、`ComponentId`、`ReloadAsync(IReloadSignal, CancellationToken)` 全计划一致。
- **遗留检查**：Task 19 中 `ExtensionManager.InitializeAsync` 幂等性为前置条件，执行时需验证；`McpLoader` 中 `logger` 字段（原实现通过 services 解析）需按原 LoadAsync 模式解析。