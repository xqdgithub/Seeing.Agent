# LLM Provider 抽象与注册表设计（v2 修订版）

**日期:** 2026-08-05（v2：架构审查后修订）
**状态:** 已修订，待用户最终确认
**范围:** 核心抽象与注册表、模型目录聚合到 Provider、运行时注册 API

> v2 修订说明：根据架构审查修订——`GetClient()` 同步化；全局 `Models`/`ModelScope.Models` 确认移除并改为显式迁移；模型 CRUD 持久化目标重定义为 `Providers[id].Models`；补全启动时序、刷新管线、`GetProviders()` 返回类型、`MaxRetries`、endpoint 查询、扩展卸载等遗漏点。

---

## 1. 背景与问题

当前 LLM 调用链为 `LlmService → IProviderManager → ILlmClientFactory → ILlmClient`，内置 `OpenAiClient` / `AnthropicClient` 两个实现，`ProviderManager` 基于 `seeing.json` 的 `Providers` 节（`ProviderConfig`）创建并缓存客户端。

现有设计的主要限制：

1. **`ProviderType` 是封闭枚举**（仅 OpenAI / Anthropic），新增协议需修改核心库。
2. **逻辑 Provider 与协议适配器未分离**：配置里 `type: OpenAI` + 自定义 `baseURL` 可以接兼容接口（DeepSeek、Azure、Ollama 等），但运行时这些 Provider 都表现为 `ProviderType.OpenAI`，无法区分身份与能力。
3. **扩展包无法注册 LLM Provider**：`IExtension` 可注册 Agent/Tool/Hook/MCP/Skill/Command，但没有 Provider 入口。
4. **模型只能来自配置文件**：扩展 Provider 的模型可能是动态获取的（如 `/v1/models` 拉取），现有 `ModelConfigManager` 只认配置来源（全局 `Models`、`ModelScope.Models`、`Provider.Models` 三处合并）。

## 2. 目标

- 每个 Provider（内置或扩展）在架构上是一等公民，由统一的 Provider 注册表管理。
- 内置两个协议 Client 继续可用；扩展可以**包装内置 Client**（简单兼容型）或**实现全新 Client**（全新协议型）。
- `ProviderConfig` 不再作为扩展 Provider 的契约依赖，仅为内置配置驱动路径的实现细节。
- 可用模型由 Provider 抽象方法提供，支持静态与动态来源。
- 支持运行时注册/注销 Provider。
- 移除全局 `Models` / `ModelScope.Models` 模型来源（见 §8 迁移说明）。

非目标：不改变 `ILlmClient` 接口本身；不改变 `seeing.json` 的 `Providers` 节格式；不改变 Hook、权限、TokenBudget 等旁路机制。

## 3. 核心抽象

### 3.1 ILlmProvider 与 LlmProviderBase

一个 Provider = 一个扩展单元。身份、Client 提供、模型列举、连接测试都归属该接口。不依赖 `ProviderConfig`。

```csharp
public interface ILlmProvider
{
    /// <summary>Provider ID（注册表唯一键，如 openai、anthropic、deepseek）</summary>
    string Id { get; }

    /// <summary>显示名称（可选）</summary>
    string? Name { get; }

    /// <summary>获取该 Provider 的 LLM 客户端（同步；实现方负责创建与缓存）</summary>
    ILlmClient GetClient();

    /// <summary>列举可用模型（可静态定义，也可动态从服务端拉取）</summary>
    Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken ct = default);

    /// <summary>测试连接是否可用</summary>
    Task<bool> TestConnectionAsync(string modelId, CancellationToken ct = default);

    /// <summary>流式请求失败时的最大重试次数（供 LlmService 消费）</summary>
    int MaxRetries { get; }
}
```

**`GetClient()` 为同步的理由**：内置 Client 创建全程同步（`DefaultLlmClientFactory.Create`、`OpenAiClient`/`AnthropicClient` 构造函数均同步），且消费侧 `IProviderManager.GetClient` / `LlmService.PrepareClientRequest` 为同步调用链。契约若异步化，ProviderManager 内部必然出现 sync-over-async（仓库反模式，Blazor Server 存在死锁风险）。需要异步初始化的 Provider（如先拉取元数据再建 Client）必须在**注册前自行预热**完成初始化。

配套抽象基类 `LlmProviderBase`（遵循仓库 `AgentBase`/`ToolBase` 惯例，接口保持纯净、默认实现放基类）：

- `MaxRetries` 默认 3（virtual，可覆盖）。
- 提供 `CreateBuiltInClient(ProviderConfig)` 辅助：通过 DI 的 `ILlmClientFactory` 创建内置 Client，使扩展能复用命名 HttpClient "LlmClient"（含 SSL 连接池修复），覆盖 §4 的包装场景（含 `AnthropicClient` 需要 HttpClient 的问题）。

生命周期约定：实现方可选实现 `IAsyncDisposable`，注册表在注销时负责释放（见 §3.2 竞态约定）。

刷新约定：动态模型的 Provider 自行决定刷新策略（每次调用 / TTL 缓存 / 显式自定义刷新方法），框架不强制；目录聚合层按 §5 管线消费。

**已知限制（P2，本期不解决）**：`ILlmClient.ProviderType` 仍为封闭枚举；全新协议的 Client 需报告最接近的内置枚举值。UI 展示层改用 `ProviderInfo.Name`/`Source`（见 §6.3），不依赖 `ProviderType` 区分身份。后续如需协议标识，可在 ProviderInfo 增加 `Kind` 字符串字段。

### 3.2 IProviderRegistry

Provider 注册表，ProviderManager 的底层管理结构。

```csharp
public interface IProviderRegistry
{
    /// <summary>所有已注册 Provider（键为 Provider Id）</summary>
    IReadOnlyDictionary<string, ILlmProvider> GetProviders();

    /// <summary>获取指定 Provider</summary>
    ILlmProvider? GetProvider(string providerId);

    /// <summary>注册 Provider（同 Id 后注册覆盖先注册，记 warning）</summary>
    /// <param name="ownerExtensionId">来源扩展 ID（内置/配置驱动为 null），用于扩展卸载时反注册</param>
    void Register(ILlmProvider provider, string? ownerExtensionId = null);

    /// <summary>注销 Provider；若其实现 IAsyncDisposable 则释放</summary>
    bool Unregister(string providerId);

    /// <summary>注销指定扩展拥有的全部 Provider</summary>
    int UnregisterByOwner(string ownerExtensionId);

    /// <summary>注册表变更事件（增/删/覆盖）</summary>
    event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;
}
```

实现要求：

- 线程安全：注册表读写使用快照替换（`ImmutableDictionary` 或等价机制），读路径无锁。
- 同名冲突策略：**后注册覆盖先注册 + warning 日志**，与框架内现有「工具注册最后 wins」一致；被覆盖的 Provider 若实现 `IAsyncDisposable` 同样释放。
- 释放竞态约定（P2，接受现状）：`Unregister` 从注册表移除后即释放；已被调用方持有的 `ILlmClient` 实例不受影响，不强制打断在途请求；与另一线程正在执行的 `GetClient()` 的释放竞态不做引用计数（释放后 `GetClient()` 不再可达，已取出者视为调用方责任）。

### 3.3 ProviderConfig 的角色变化

`ProviderConfig` 与 `ProviderType` 枚举保留，但降级为**内置配置驱动路径的实现细节**：

- 仅 `ConfiguredLlmProvider`（见 §4）、`ILlmClientFactory`、配置持久化 API（`SaveProviderAsync` 等）内部使用。
- 不出现在 `ILlmProvider` / `IProviderRegistry` 契约中。
- `seeing.json` 的 `Providers` 节格式不变，现有 `type: OpenAI/Anthropic` 配置行为不变。

## 4. 配置驱动的内置 Provider

新增 `ConfiguredLlmProvider : LlmProviderBase`，把「写在 `seeing.json` 的 Provider」变成注册表里的普通一员：

- 构造参数：`ProviderConfig`、`ILlmClientFactory`、`ILogger`。
- `Id = config.Id`，`Name = config.Name`，`MaxRetries = config.MaxRetries`。
- `GetClient()`：委托 `ILlmClientFactory.Create(config)`，结果用 `Lazy<ILlmClient>`（`ExecutionAndPublication`）保证单例与并发安全。
- `GetModelsAsync()`：返回 `config.Models` 的值集合（每项补齐 `ModelConfig.Provider = Id`）；未配置时返回空列表。**该方法无 IO，同步完成**（返回 `Task.FromResult`），这是 §5 初始目录可同步就绪的前提。
- `TestConnectionAsync()`：委托 Client 的 `TestConnectionAsync`。

**包装与扩展的两种形态**：

1. **简单兼容型**（如 DeepSeek、Ollama、Azure OpenAI）：扩展 Provider 在 `GetClient()` 内通过 `LlmProviderBase.CreateBuiltInClient(...)`（或直接解析 DI 的 `ILlmClientFactory`）创建 `OpenAiClient`/`AnthropicClient`，传入自行调整的 `ProviderConfig`（Id、BaseUrl、Headers 等）。不新增包装类型，且复用命名 HttpClient 连接池。
2. **需要改行为型**：扩展实现自定义 `ILlmClient`（可对内置 Client 做委托/装饰），或实现完全新协议。`ILlmProvider` 契约对两者一视同仁。

**模型来源统一规则**：模型一律由对应 Provider 提供（配置驱动 Provider 来自 `ProviderConfig.Models`，扩展 Provider 来自其 `GetModelsAsync()`）。全局 `Models` / `ModelScope.Models` **确认移除**，不再参与聚合，配置文件中若存在这两节将记 deprecation warning 并忽略（迁移说明见 §8）。

## 5. 模型目录与刷新管线

### 5.1 聚合规则

- 目录 = 所有已注册 `ILlmProvider.GetModelsAsync()` 结果的并集。目录键统一 `providerId/modelId`（沿用 `ModelRef.Format`）。
- 单个 Provider 的 `GetModelsAsync` 抛异常：记 error 日志，该 Provider 按「无模型」参与聚合，不影响其他 Provider。
- 聚合结果缓存；`GetModels()`/`GetModel()`/`GetModelsByProvider()` 等同步读取 API 只读缓存。

### 5.2 初始加载（消除启动竞态）

`ModelConfigManager` 构造时**同步**构建初始目录：

1. 直接读取 `SeeingAgentOptions.Providers[*].Models`（纯内存、无 IO）填充配置驱动部分——不依赖注册表是否已装配，无初始化时序竞态。
2. 此时注册表中可能尚无扩展 Provider，属正常；扩展 Provider 注册后通过 §5.3 管线异步补齐。

### 5.3 刷新管线（串行化 + 版本戳）

`ProvidersChanged`、配置热更（`Providers` 节变更）、Provider 自刷新请求统一进入一个**单消费者刷新队列**：

- 内部使用 `Channel<RefreshRequest>`（或 `SemaphoreSlim` 串行化）+ 单调递增版本戳；不允许 `async void` 事件处理器（仓库禁止项）。
- 每次刷新开始取当前版本号；完成后仅当版本号仍为最新才替换缓存（stale 结果丢弃），避免慢刷新覆盖新刷新。
- 刷新完成后触发 `ModelConfigChanged` 事件（`ChangeType.Updated`，全量语义），供 WebUI（`MainLayout`、`ModelsPage`）等订阅方刷新。
- 顺带修复 `ModelConfigManager._providerIndex` 懒加载 `??=` 的线程安全问题。

### 5.4 解析规则

- `GetModel(modelId)` 沿用 `ModelRef.Parse`，但「已知 Provider 集合」改为**注册表当前 Provider Id 集合**，保证扩展 Provider 的 `provider/model` 前缀引用可正确解析，同时继续避免 HuggingFace 风格 ID（`Qwen/Qwen3-...`）被误判为 Provider 前缀。
- 「已知 Provider 集合」切换的**全部消费点**（必须同步修改）：
  - `ModelConfigManager`：`GetModel` 内 2 处、`EnsureModelDefaults` 内 3 处。
  - `AgentRuntimeManager.SetSessionModelOverrideAsync`（当前用 `SeeingAgentOptions.Providers.Keys` 做前缀试探）。
  - WebUI：`AgentConfigForm.razor`、`JobEditorModal.razor`（当前用 `ProviderManager.GetProviders().Keys`）。

### 5.5 模型 CRUD 持久化目标（重定义）

现状 `AddModelAsync`/`UpdateModelAsync`/`DeleteModelAsync`/`SaveModelsAsync` 写全局 "Models" 节，全局模型移除后必须改向：

- CRUD 一律写 **`Providers[providerId].Models`** 节（经 `UnifiedConfigManager.SaveSectionAsync`）。
- `providerId` 来源：`AddModelAsync`/`SaveModelsAsync` 增加 `providerId` 参数（或要求 `ModelConfig.Provider` 必填且存在于注册表）；`UpdateModelAsync`/`DeleteModelAsync` 从模型引用解析（无前缀时按目录现有归属解析）。
- 目标 Provider 为**扩展注册**时：no-op + warning（扩展模型不可持久化）。
- WebUI `ModelsPage.razor` 相应调整：新增/编辑模型必须选定 Provider；保存后走 §5.3 管线刷新。

## 6. ProviderManager 重构与接线

### 6.1 启动装配时序（唯一注册点，禁止双重注册）

1. DI 注册（`RegisterLlmServices`）：`IProviderRegistry`（Singleton，无依赖）、`ILlmClientFactory`、`ConfiguredLlmProvider` 不作为服务而是**由 ProviderManager 构造时创建并注册**。
2. `ProviderManager` 构造函数：读取 `SeeingAgentOptions.Providers`，为每条目创建 `ConfiguredLlmProvider` 并 `Register(provider, ownerExtensionId: null)`。**配置驱动 Provider 只在此处注册**。
3. 扩展 Provider：`ExtensionManager.RegisterComponents` 阶段调用 `IExtension.GetProviders()`（新增可选成员，默认空集合，默认接口实现风格与现有成员一致），逐个 `Register(provider, extension.Id)`。
4. `ModelConfigManager` 构造：按 §5.2 同步建初始目录，并订阅 `IProviderRegistry.ProvidersChanged` 启动 §5.3 管线。

依赖关系：`ProviderRegistry`（无依赖）← `ProviderManager`（→ `ILlmClientFactory`、`IModelConfigManager`）；`ModelConfigManager` → `IProviderRegistry`（仅订阅事件与读 Id 集合）。无新增循环依赖；现有 `ModelManager → IAgentStore` 规避环不变。

### 6.2 配置热更

`UnifiedConfigManager.ConfigChanged` 涉及 `Providers` 节时，仅对**配置驱动** Provider 做增/删/重建：

- 重建条件修复（顺带修复现有缺陷）：除 `ProviderType` 变化外，`ApiKey`/`BaseUrl`/`Headers`/`Timeout` 变化同样重建（现状同类型下改 Key 不生效）。
- 扩展注册的 Provider 不受配置热更影响；配置条目与扩展 Provider 同 Id 时按 §3.2 覆盖规则处理。

### 6.3 对外接口（定稿）

`IProviderManager` 调整如下（这是一处**公开 API 破坏**，随本次主版本变更一并发布）：

- `GetProviders()` → 返回 `IReadOnlyDictionary<string, ProviderInfo>`。
- `GetProvider(id)` → 返回 `ProviderInfo?`。
- `GetClient` / `GetClientForModel` / `TestConnectionAsync` / `GetDefaultProvider` 签名不变，内部走注册表 + `ILlmProvider.GetClient()`。
- `SaveProviderAsync` / `DeleteProviderAsync` / `SetDefaultProviderAsync` 保留（本质为配置写操作，参数仍是 `ProviderConfig`）；对扩展注册的 Provider Id 调用时记 warning 并 no-op。

`ProviderInfo` DTO（新增公开类型）：

```csharp
public record ProviderInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>来源：Configured（配置文件）/ Extension（扩展包）</summary>
    public ProviderSource Source { get; init; }
    /// <summary>来源扩展 ID（Source=Extension 时有值）</summary>
    public string? OwnerExtensionId { get; init; }
    public int MaxRetries { get; init; }
}
```

已知调用方改造清单：`LlmService.cs`（MaxRetries 取值改走 `ProviderInfo`）、WebUI `ModelsPage.razor`（枚举、编辑表单区分 Source）、`AgentConfigForm.razor`、`JobEditorModal.razor`。

### 6.4 扩展卸载接线

`ExtensionManager.DeactivateAsync`（现状不反注册组件，本次一并补齐 Provider 部分）：调用 `IProviderRegistry.UnregisterByOwner(extension.Id)`；释放由注册表按 §3.2 约定执行。

## 7. IProviderEndpointLookup 适配

Memory 包的 embedding 通道（`ProviderEmbeddingService`、`EmbeddingConnectionTester`）经 `IProviderEndpointLookup` 直读 `SeeingAgentOptions.Providers` 取 endpoint，扩展 Provider 对其不可见。方案：

- 新增可选接口 `IProviderEndpointInfo { string? BaseUrl; string? ApiKey; }`，扩展 Provider 可选择实现以暴露 endpoint。
- `OptionsProviderEndpointLookup` 改为先查注册表：Provider 实现 `IProviderEndpointInfo` 则用其值；否则（配置驱动）回退读 `SeeingAgentOptions.Providers`。
- 未实现该接口的扩展 Provider 在 embedding 通道表现为「endpoint 未配置」，与现状对未知 Provider 的行为一致。

## 8. 兼容性与迁移

| 项 | 变化 | 影响与迁移 |
|----|------|-----------|
| `ILlmClient` / 请求响应模型 | 不变 | 无 |
| `seeing.json` 的 `Providers` 节 | 格式与行为不变 | 无 |
| 全局 `Models` / `ModelScope.Models` 节 | **移除聚合**，存在时记 deprecation warning 并忽略 | **行为破坏**：用户需把模型条目迁移到对应 `Providers[id].Models`；迁移指南写入变更日志 |
| `IProviderManager.GetProviders()`/`GetProvider()` 返回类型 | `ProviderConfig` → `ProviderInfo` | **公开 API 源码+二进制破坏**；已知调用方按 §6.3 清单改造 |
| 模型 CRUD 持久化目标 | 全局 "Models" 节 → `Providers[id].Models` | **行为破坏**；WebUI 模型管理页同步改造 |
| `LlmService` 公共方法 | 签名不变 | 无（内部改走 `ProviderInfo.MaxRetries`） |
| `IExtension` | 新增 `GetProviders()` 默认接口成员 | 源码兼容 |
| `ProviderType` / `ProviderConfig` / `ILlmClientFactory` | 保留公开 | 文档标注为「内置路径」；`ILlmClientFactory` 同时是扩展包装内置 Client 的官方入口 |

**测试现状风险提示**：现有测试全部 mock `IModelConfigManager` 或测纯静态类，**没有 `ProviderManager`/`ModelConfigManager` 的直接测试**——本次重构前必须先补表征测试（见 §9），否则零回归网。

## 9. 测试计划

| 阶段 | 测试目标 | 用例 |
|------|----------|------|
| 重构前 | **表征测试**（锁定现状行为） | `ProviderManager`：客户端创建/缓存/热更重建；`ModelConfigManager`：三来源合并、CRUD 写全局 Models 节、GetModel 解析优先级 |
| 1 | `ProviderRegistry` | 注册/注销/同名覆盖、按 Owner 注销、ProvidersChanged 事件、并发读一致性、Unregister 释放 IAsyncDisposable |
| 1 | `ConfiguredLlmProvider` | `GetClient()` 单例（Lazy 并发）、模型来自 `config.Models` 且同步完成、MaxRetries 透传、TestConnection 委托 |
| 2 | 模型目录聚合 | 多 Provider 并集、扩展动态模型可见、单 Provider 异常不影响整体、初始目录同步就绪（无启动竞态） |
| 2 | 刷新管线 | 串行化、版本戳丢弃 stale 结果、刷新完成触发 ModelConfigChanged、无 async void |
| 2 | 模型解析 | 扩展 Provider 的 `provider/model` 前缀解析、HuggingFace 风格 ID 不误判、裸 ID 解析；§5.4 全部消费点切换正确 |
| 2 | 模型 CRUD | 写入 `Providers[id].Models`、扩展 Provider no-op + warning、无前缀引用解析归属 |
| 3 | 扩展接线 | `IExtension.GetProviders()` 注册出现在注册表与目录；`DeactivateAsync` 反注册 |
| 3 | 配置热更 | Providers 节变更后配置驱动 Provider 增删重建（含 ApiKey 变更重建）；扩展 Provider 不受影响 |
| 3 | endpoint 查询 | `IProviderEndpointInfo` 实现优先、配置回退、未实现时的未知 Provider 行为 |
| 4 | 回归 | 全部既有测试通过；WebUI 三处页面/组件手工验证 |

测试位置：`tests/Seeing.Agent.Tests/Llm/`。

## 10. 实施顺序（供 writing-plans 参考）

0. **表征测试先行**：为 `ProviderManager` / `ModelConfigManager` 现状行为补测试。
1. `ILlmProvider` / `LlmProviderBase` / `IProviderRegistry` / `ProviderInfo` / `ProviderSource` / 事件参数定义 + 注册表实现 + 单测。
2. `ConfiguredLlmProvider` + `IExtension.GetProviders()` + `ExtensionManager` 注册/卸载接线 + 单测。
3. `ProviderManager` 重构走注册表 + `IProviderManager` 接口切换 `ProviderInfo` + 热更重建条件修复 + 已知调用方（LlmService、WebUI 三处）改造。
4. 模型目录改造：聚合数据源切换、初始同步加载、刷新管线（Channel + 版本戳）、全局 `Models`/`ModelScope` 移除 + deprecation warning、CRUD 目标重定义、§5.4 全部消费点切换、`IProviderEndpointLookup` 适配 + 单测。
5. 全量回归：`dotnet test` + WebUI 手工验证。
