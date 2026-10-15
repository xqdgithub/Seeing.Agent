# LLM Provider 抽象与注册表 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 引入 `ILlmProvider` + `IProviderRegistry`，统一内置（配置驱动）与扩展包注册的 Provider，模型目录聚合到 Provider，支持运行时注册。

**Architecture:** `ProviderManager` 重构为注册表生命周期管理；`seeing.json` 的 `Providers` 条目实例化为 `ConfiguredLlmProvider`；扩展通过 `IExtension.GetProviders()` 注册；模型目录从「配置合并」改为「Provider 聚合 + 版本化刷新管线」。

**Tech Stack:** .NET 10, C# 12, xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12, `System.Threading.Channels`, `System.Collections.Immutable`

**Spec:** `docs/superpowers/specs/2026-08-05-llm-provider-abstraction-design.md`

## Global Constraints

- **全程不执行任何 `git commit`**（用户明确要求；所有「提交」步骤仅 `git add` 或跳过）
- 目标框架 `net10.0`；中央包管理（CPM），不新增包版本除非必须
- 异步方法统一 `Async` 后缀；私有字段 `_camelCase`；接口不加默认实现（默认实现放 `LlmProviderBase`）
- **禁止 `async void`**（事件处理器用 `Channel<T>` 单消费者或 `SemaphoreSlim` 串行化）
- **禁止 sync-over-async**（`GetClient()` 保持同步；不引入 `.GetAwaiter().GetResult()`）
- 结构化日志 `{PropertyName}` 格式
- 测试命名 `{方法}_{场景}_Should{预期结果}` 或 AAA 注释分区

---

### Task 0: 表征测试（锁定 ProviderManager / ModelConfigManager 现状行为）

**Files:**
- Create: `tests/Seeing.Agent.Tests/Llm/ProviderManagerCharacterizationTests.cs`
- Create: `tests/Seeing.Agent.Tests/Llm/ModelConfigManagerCharacterizationTests.cs`

**Interfaces:**
- Consumes: `ProviderManager`, `ModelConfigManager`, `UnifiedConfigManager`, `ProviderConfig`, `ModelConfig`
- Produces: 重构前回归网（后续任务破坏这些测试即说明行为变更）

- [ ] **Step 1: 写 ProviderManager 表征测试**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ProviderManagerCharacterizationTests
{
    [Fact]
    public void GetClient_ConfiguredProvider_ReturnsClient()
    {
        // Arrange
        var options = new SeeingAgentOptions
        {
            Providers = { ["openai"] = PredefinedProviders.OpenAI("sk-test") }
        };
        var configManager = CreateConfigManager(options);
        var factory = new DefaultLlmClientFactory(...); // 或 Mock<ILlmClientFactory>
        var modelManager = Mock.Of<IModelConfigManager>();
        var sut = new ProviderManager(configManager, factory, modelManager, NullLogger<ProviderManager>.Instance);

        // Act
        var client = sut.GetClient("openai");

        // Assert
        client.Should().NotBeNull();
        client!.ProviderType.Should().Be(ProviderType.OpenAI);
    }

    [Fact]
    public void GetClient_UnknownProvider_ReturnsNull() { /* ... */ }

    [Fact]
    public void GetProviders_ReturnsConfiguredProviders() { /* ... */ }

    [Fact]
    public async Task TestConnectionAsync_ConfigChanged_RecreatesClientOnTypeChange() { /* 现状：仅 Type 变化时重建 */ }
}
```

- [ ] **Step 2: 写 ModelConfigManager 表征测试**

```csharp
public class ModelConfigManagerCharacterizationTests
{
    [Fact]
    public void GetModels_MergesGlobalModelsModelScopeAndProviderModels() { /* 三来源合并现状 */ }

    [Fact]
    public void GetModel_BareId_FallsBackToAnyProvider() { /* 裸 ID 解析现状 */ }

    [Fact]
    public async Task AddModelAsync_WritesToGlobalModelsSection() { /* 现状：写 "Models" 节 */ }

    [Fact]
    public async Task DeleteModelAsync_RemovesFromGlobalModelsSection() { /* 现状 */ }
}
```

- [ ] **Step 3: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CharacterizationTests"`
Expected: PASS

- [ ] **Step 4: 暂存（不提交）**

```bash
git add tests/Seeing.Agent.Tests/Llm/ProviderManagerCharacterizationTests.cs tests/Seeing.Agent.Tests/Llm/ModelConfigManagerCharacterizationTests.cs
```

---

### Task 1: 核心抽象（ILlmProvider / LlmProviderBase / IProviderRegistry / ProviderInfo）

**Files:**
- Create: `src/Seeing.Agent/Llm/ILlmProvider.cs`
- Create: `src/Seeing.Agent/Llm/LlmProviderBase.cs`
- Create: `src/Seeing.Agent/Llm/IProviderRegistry.cs`
- Create: `src/Seeing.Agent/Llm/ProviderInfo.cs`
- Create: `src/Seeing.Agent/Llm/ProviderSource.cs`
- Create: `src/Seeing.Agent/Llm/ProvidersChangedEventArgs.cs`
- Test: `tests/Seeing.Agent.Tests/Llm/ProviderRegistryTests.cs`

**Interfaces:**
- Consumes: `ILlmClient`, `ModelConfig`
- Produces:
  - `ILlmProvider { string Id; string? Name; ILlmClient GetClient(); Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken); Task<bool> TestConnectionAsync(string, CancellationToken); int MaxRetries { get; } }`
  - `LlmProviderBase : ILlmProvider`（`MaxRetries => 3`，`CreateBuiltInClient(ProviderConfig)`）
  - `IProviderRegistry { IReadOnlyDictionary<string, ILlmProvider> GetProviders(); ILlmProvider? GetProvider(string); void Register(ILlmProvider, string? ownerExtensionId = null); bool Unregister(string); int UnregisterByOwner(string); event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged; }`
  - `ProviderInfo { string Id; string? Name; ProviderSource Source; string? OwnerExtensionId; int MaxRetries; }`
  - `ProviderSource { Configured, Extension }`

- [ ] **Step 1: 写 IProviderRegistry 失败测试**

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ProviderRegistryTests
{
    [Fact]
    public void Register_NewProvider_AddsToRegistry()
    {
        var sut = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new Mock<ILlmProvider>();
        provider.Setup(p => p.Id).Returns("test");

        sut.Register(provider.Object);

        sut.GetProvider("test").Should().BeSameAs(provider.Object);
    }

    [Fact]
    public void Register_DuplicateId_OverridesAndLogsWarning() { /* 后注册覆盖 */ }

    [Fact]
    public void Unregister_ExistingProvider_RemovesAndDisposes() { /* IAsyncDisposable 释放 */ }

    [Fact]
    public void UnregisterByOwner_ExtensionProviders_RemovesAllOwned() { /* ownerExtensionId 反注册 */ }

    [Fact]
    public void ProvidersChanged_OnRegister_RaisesEvent() { /* 事件触发 */ }

    [Fact]
    public async Task ConcurrentReads_DuringRegister_AreConsistent() { /* 快照替换 */ }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ProviderRegistryTests"`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现 ILlmProvider / LlmProviderBase / IProviderRegistry / ProviderInfo / ProviderSource / ProvidersChangedEventArgs**

`ILlmProvider.cs` / `LlmProviderBase.cs` / `IProviderRegistry.cs` / `ProviderInfo.cs` / `ProviderSource.cs` / `ProvidersChangedEventArgs.cs` 按 §3 契约实现；`ProviderRegistry` 内部用 `ImmutableDictionary<string, ILlmProvider>` + 快照替换。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ProviderRegistryTests"`
Expected: PASS

- [ ] **Step 5: 暂存（不提交）**

```bash
git add src/Seeing.Agent/Llm/ILlmProvider.cs src/Seeing.Agent/Llm/LlmProviderBase.cs src/Seeing.Agent/Llm/IProviderRegistry.cs src/Seeing.Agent/Llm/ProviderInfo.cs src/Seeing.Agent/Llm/ProviderSource.cs src/Seeing.Agent/Llm/ProvidersChangedEventArgs.cs tests/Seeing.Agent.Tests/Llm/ProviderRegistryTests.cs
```

---

### Task 2: 配置驱动 Provider（ConfiguredLlmProvider）+ 扩展入口（IExtension.GetProviders）

**Files:**
- Create: `src/Seeing.Agent/Llm/ConfiguredLlmProvider.cs`
- Modify: `src/Seeing.Agent/Core/Interfaces/IExtension.cs`（新增 `GetProviders()`）
- Modify: `src/Seeing.Agent/Extensions/ExtensionManager.cs`（注册/卸载接线）
- Test: `tests/Seeing.Agent.Tests/Llm/ConfiguredLlmProviderTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ILlmProvider`/`LlmProviderBase`/`IProviderRegistry`；`ProviderConfig`；`ILlmClientFactory`
- Produces:
  - `ConfiguredLlmProvider : LlmProviderBase`（构造：`(ProviderConfig config, ILlmClientFactory factory, ILogger logger)`；`GetClient()` Lazy 单例；`GetModelsAsync()` 同步返回 `config.Models`）
  - `IExtension.GetProviders() => IEnumerable<ILlmProvider>`（默认空）

- [ ] **Step 1: 写 ConfiguredLlmProvider 失败测试**

```csharp
public class ConfiguredLlmProviderTests
{
    [Fact]
    public void GetClient_CalledTwice_ReturnsSameInstance() { /* Lazy 单例 */ }

    [Fact]
    public async Task GetModelsAsync_ReturnsConfigModels() { /* 来自 config.Models */ }

    [Fact]
    public async Task GetModelsAsync_NoModels_ReturnsEmpty() { /* 空列表 */ }

    [Fact]
    public void MaxRetries_ReturnsConfigValue() { /* 透传 config.MaxRetries */ }

    [Fact]
    public async Task TestConnectionAsync_DelegatesToClient() { /* 委托 ILlmClient.TestConnectionAsync */ }
}
```

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现 ConfiguredLlmProvider**

```csharp
public class ConfiguredLlmProvider : LlmProviderBase
{
    private readonly ProviderConfig _config;
    private readonly ILlmClientFactory _factory;
    private readonly ILogger _logger;
    private readonly Lazy<ILlmClient> _client;

    public ConfiguredLlmProvider(ProviderConfig config, ILlmClientFactory factory, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = new Lazy<ILlmClient>(() => _factory.Create(_config), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public override string Id => _config.Id;
    public override string? Name => _config.Name;
    public override int MaxRetries => _config.MaxRetries;

    public override ILlmClient GetClient() => _client.Value;

    public override Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken ct = default)
    {
        var models = _config.Models?.Values.ToList() ?? new List<ModelConfig>();
        foreach (var m in models) m.Provider = Id;
        return Task.FromResult<IReadOnlyList<ModelConfig>>(models);
    }

    public override Task<bool> TestConnectionAsync(string modelId, CancellationToken ct = default)
        => GetClient().TestConnectionAsync(modelId, ct);
}
```

- [ ] **Step 4: 修改 IExtension.cs 新增 GetProviders()**

```csharp
/// <summary>
/// 提供的 LLM Provider（新增）
/// </summary>
IEnumerable<ILlmProvider> GetProviders() => Enumerable.Empty<ILlmProvider>();
```

- [ ] **Step 5: 修改 ExtensionManager.cs 注册/卸载接线**

在 `RegisterComponents`（或等效位置）调用 `extension.GetProviders()` 并 `registry.Register(provider, extension.Id)`；在 `DeactivateAsync` 调用 `registry.UnregisterByOwner(extension.Id)`。

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ConfiguredLlmProviderTests"`
Expected: PASS

- [ ] **Step 7: 暂存（不提交）**

---

### Task 3: ProviderManager 重构走注册表 + ProviderInfo 切换

**Files:**
- Modify: `src/Seeing.Agent/Llm/ProviderManager.cs`
- Modify: `src/Seeing.Agent/Llm/IProviderManager.cs`
- Modify: `src/Seeing.Agent/Llm/LlmService.cs`（MaxRetries 取值）
- Modify: `samples/Seeing.Agent.WebUI/Components/Pages/ModelsPage.razor`（Provider 枚举/编辑）
- Modify: `samples/Seeing.Agent.WebUI/Components/AgentConfigForm.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/JobEditorModal.razor`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（DI 注册顺序）
- Test: `tests/Seeing.Agent.Tests/Llm/ProviderManagerTests.cs`（新）+ 表征测试回归

**Interfaces:**
- Consumes: Task 1 的 `IProviderRegistry`/`ProviderInfo`；Task 2 的 `ConfiguredLlmProvider`
- Produces:
  - `IProviderManager.GetProviders() => IReadOnlyDictionary<string, ProviderInfo>`
  - `IProviderManager.GetProvider(id) => ProviderInfo?`
  - `IProviderManager.GetClient(id)` / `GetClientForModel(id)` / `TestConnectionAsync` 签名不变，内部走注册表
  - `SaveProviderAsync` / `DeleteProviderAsync` / `SetDefaultProviderAsync` 保留，对扩展 Provider no-op + warning

- [ ] **Step 1: 写新 ProviderManager 失败测试**

```csharp
public class ProviderManagerTests
{
    [Fact]
    public void Constructor_RegistersConfiguredProviders() { /* seeing.json Providers → registry */ }

    [Fact]
    public void GetProviders_ReturnsProviderInfo() { /* 返回 ProviderInfo 而非 ProviderConfig */ }

    [Fact]
    public void GetClient_ConfiguredProvider_ReturnsClientFromProvider() { /* 走 ConfiguredLlmProvider.GetClient() */ }

    [Fact]
    public async Task SaveProviderAsync_ExtensionProvider_LogsWarningAndNoOp() { /* 扩展 Provider 配置写 no-op */ }

    [Fact]
    public async Task ConfigChanged_ApiKeyChange_RecreatesConfiguredProvider() { /* 修复现状缺陷 */ }
}
```

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 重构 ProviderManager**

- 构造函数：读取 `SeeingAgentOptions.Providers`，创建 `ConfiguredLlmProvider` 并 `Register(provider, ownerExtensionId: null)`。
- `GetProviders()` / `GetProvider()` 返回 `ProviderInfo`（从 registry 投影）。
- `GetClient()` / `GetClientForModel()` 内部 `_registry.GetProvider(id)?.GetClient()`。
- 配置热更：仅对配置驱动 Provider 增删重建；重建条件含 `Type`/`ApiKey`/`BaseUrl`/`Headers`/`Timeout` 变化。
- `SaveProviderAsync` 等：检查 registry 中该 Id 的 `Source`，为 `Extension` 时 warning + no-op。

- [ ] **Step 4: 修改 LlmService.cs MaxRetries 取值**

```csharp
var maxRetries = _providerManager.GetProvider(modelConfig.Provider)?.MaxRetries ?? 3;
```

- [ ] **Step 5: 修改 WebUI 三处调用点**

`ModelsPage.razor` / `AgentConfigForm.razor` / `JobEditorModal.razor` 改用 `ProviderInfo` 字段（Id/Name/Source/ModelCount 等）。

- [ ] **Step 6: 修改 DI 注册顺序**

`ServiceCollectionExtensions.RegisterLlmServices`：先注册 `IProviderRegistry`（Singleton），再注册 `IProviderManager`（构造时创建 `ConfiguredLlmProvider` 并注册进 registry）。

- [ ] **Step 7: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ProviderManagerTests|FullyQualifiedName~CharacterizationTests"`
Expected: PASS（表征测试可能因 `GetProviders()` 返回类型变化而失败，此时更新表征测试并标注为「预期行为变更」）

- [ ] **Step 8: 暂存（不提交）**

---

### Task 4: 模型目录聚合 + 刷新管线 + CRUD 重定义 + 全局模型移除

**Files:**
- Modify: `src/Seeing.Agent/Llm/ModelConfigManager.cs`
- Modify: `src/Seeing.Agent/Llm/IModelConfigManager.cs`（CRUD 签名）
- Modify: `src/Seeing.Agent/Llm/ModelRef.cs`（已知 Provider 来源切换）
- Modify: `src/Seeing.Agent/AgentRuntimeManager.cs`（前缀试探）
- Modify: `src/Seeing.Agent/Llm/OptionsProviderEndpointLookup.cs`（IProviderEndpointInfo 适配）
- Modify: `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs`（全局 Models/ModelScope 标记 obsolete）
- Modify: `src/Seeing.Agent/Configuration/UnifiedConfigManager.cs`（"Models"/"ModelScope" 节处理）
- Modify: `samples/Seeing.Agent.WebUI/Components/Pages/ModelsPage.razor`（CRUD 目标 Provider 选择）
- Test: `tests/Seeing.Agent.Tests/Llm/ModelCatalogAggregationTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IProviderRegistry`；Task 3 的 `ProviderManager`
- Produces:
  - `IModelConfigManager.GetModels()` 聚合所有 Provider 的 `GetModelsAsync()`
  - `AddModelAsync(string providerId, string modelId, ModelConfig config, ...)` 写 `Providers[providerId].Models`
  - `UpdateModelAsync`/`DeleteModelAsync` 从模型引用解析归属 Provider
  - 刷新管线：`Channel<RefreshRequest>` + 版本戳
  - `IProviderEndpointInfo { string? BaseUrl; string? ApiKey; }`

- [ ] **Step 1: 写聚合失败测试**

```csharp
public class ModelCatalogAggregationTests
{
    [Fact]
    public void GetModels_AggregatesAllRegisteredProviders() { /* 多 Provider 并集 */ }

    [Fact]
    public async Task GetModels_ExtensionProvider_DynamicModelsVisibleAfterRefresh() { /* 动态模型可见 */ }

    [Fact]
    public async Task GetModels_OneProviderThrows_OthersStillVisible() { /* 异常隔离 */ }

    [Fact]
    public void Constructor_InitialCatalog_SynchronouslyReady() { /* 无启动竞态 */ }

    [Fact]
    public async Task Refresh_StaleResultDiscarded_LatestWins() { /* 版本戳 */ }

    [Fact]
    public async Task AddModelAsync_WritesToProviderModelsSection() { /* CRUD 重定义 */ }

    [Fact]
    public async Task AddModelAsync_ExtensionProvider_LogsWarningAndNoOp() { /* 扩展 no-op */ }

    [Fact]
    public void GetModel_ExtensionProviderPrefix_ResolvesCorrectly() { /* 前缀解析 */ }
}
```

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现 ModelConfigManager 聚合改造**

- 构造时同步读取 `SeeingAgentOptions.Providers[*].Models` 建初始目录（不依赖 registry）。
- 订阅 `IProviderRegistry.ProvidersChanged` → 发送 `RefreshRequest` 到 `Channel<RefreshRequest>`（单消费者后台任务）。
- 刷新逻辑：版本戳递增 → 并发 await 所有 Provider `GetModelsAsync()`（异常隔离）→ 仅当版本号仍最新才替换 `_modelCache` → 触发 `ModelConfigChanged(Updated)`。
- 修复 `_providerIndex` 懒加载线程安全。
- `GetModel()` 的 `knownProviders` 改为 `_registry.GetProviders().Keys`。

- [ ] **Step 4: 实现 CRUD 重定义**

`AddModelAsync`/`UpdateModelAsync`/`DeleteModelAsync`/`SaveModelsAsync` 写 `Providers[providerId].Models`；`providerId` 为扩展 Provider 时 no-op + warning。

- [ ] **Step 5: 移除全局 Models/ModelScope 聚合**

`RefreshCache` 删除全局 Models 和 ModelScope.Models 合并逻辑；`SeeingAgentOptions.Models`/`ModelScope` 标记 `[Obsolete]`；`UnifiedConfigManager` 对这两节记 deprecation warning。

- [ ] **Step 6: 切换 ModelRef 已知 Provider 全部消费点**

`ModelConfigManager`（3 处）、`AgentRuntimeManager.SetSessionModelOverrideAsync`、WebUI `AgentConfigForm.razor`/`JobEditorModal.razor` 改用 registry Id 集合。

- [ ] **Step 7: 实现 IProviderEndpointLookup 适配**

新增 `IProviderEndpointInfo`；`OptionsProviderEndpointLookup` 先查 registry 中 Provider 是否实现该接口，否则回退读 `SeeingAgentOptions.Providers`。

- [ ] **Step 8: 修改 WebUI ModelsPage CRUD**

新增/编辑模型必须选定 Provider；保存后触发刷新。

- [ ] **Step 9: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelCatalogAggregationTests|FullyQualifiedName~ModelManagerTests|FullyQualifiedName~ModelRefTests"`
Expected: PASS（表征测试因全局模型移除和 CRUD 目标变更而失败，更新并标注）

- [ ] **Step 10: 暂存（不提交）**

---

### Task 5: 全量回归 + WebUI 手工验证

**Files:**
- 无新增；验证现有测试与 WebUI

**Interfaces:**
- Consumes: 前 4 个任务全部产出
- Produces: 可合并的完整实现

- [ ] **Step 1: 运行全量测试**

Run: `dotnet test`
Expected: 全部 PASS（或明确标注为预期行为变更）

- [ ] **Step 2: 构建解决方案**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 Error

- [ ] **Step 3: 启动 WebUI 手工验证**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

验证点：
- 模型列表正常显示（来自 `Providers[*].Models`）
- 新增/编辑/删除模型写入对应 Provider 的 `Models` 节
- 会话模型选择正常
- 扩展 Provider（如有测试扩展）注册后模型可见

- [ ] **Step 4: 更新表征测试标注**

在表征测试文件头部添加注释：哪些用例是「预期行为变更」而非回归失败。

- [ ] **Step 5: 最终暂存（不提交）**

```bash
git add -A
```

---

## 风险与回滚

| 风险 | 缓解 |
|------|------|
| 表征测试不足导致回归未发现 | Task 0 覆盖核心路径；Task 5 全量回归 |
| 刷新管线并发 bug | 版本戳 + 串行化；单元测试覆盖 stale 丢弃 |
| WebUI 模型页破坏 | Task 4 Step 8 同步改造；Task 5 手工验证 |
| 全局模型移除破坏存量用户 | §8 迁移指南写入变更日志；deprecation warning 提前一个版本 |

## 后续（不在本计划）

- `ProviderInfo.Kind` 协议标识（如需）
- `IProviderRegistry` 运行时注册 API 的完整生命周期管理（引用计数、宽限期）
- 扩展 Provider 的模型持久化（如需）
