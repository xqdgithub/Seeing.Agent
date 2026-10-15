# Seeing.Agent.Memory 解耦改造方案

**版本**: 1.1  
**日期**: 2026-07-18  
**状态**: 决策已定（方案 B：核心库窄接口）  
**前置**: `2026-07-18-memory-pipeline-design.md`（功能已基本落地；本方案解决模块边界）

---

## 〇、架构决策（2026-07-18）

**选定：方案 B — 在 `Seeing.Agent` 核心库内抽取窄公共接口，供 Memory / ACP / Scheduler 等模块使用。**

| 备选 | 结论 |
|------|------|
| A. 每模块自有端口 | 可作薄包装，但配置/LLM 契约以核心窄接口为准，避免各模块重复发明 |
| **B. 核心库内窄接口** | **采用**：不新建 Contracts NuGet；缩小依赖面 |
| C. 独立 `Seeing.Contracts` 包 | 暂不做；待跨宿主/独立发版再议 |

因此：

- **禁止** Memory Core 直接依赖 `UnifiedConfigManager`、完整 `ILlmService`。
- **允许** Memory Integration（及未来其它插件）依赖核心窄接口：`IConfigSectionStore`、`ITextCompletion`（及可选 `IProviderEndpointLookup`）。
- Memory 可保留极薄的 `IMemoryOptionsStore` = 对 `IConfigSectionStore` + `"Memory"` 节的封装，便于 UI/Options 绑定。

---

## 一、目标与非目标

### 1.1 目标

把 Memory 做成**相对独立的可选模块**：

1. **宿主零胶水**：WebUI / TUI / Host 只需 `AddMemoryServices()`（或 Plugins 加载），不负责 Register Tool/Hook。
2. **Core 不依赖核心宽实现**：抽取/演化只依赖核心窄接口或 Memory 薄封装；禁止 `UnifiedConfigManager` / 完整 `ILlmService`。
3. **核心提供可复用窄能力（方案 B）**：`IConfigSectionStore`、`ITextCompletion` 落在 `Seeing.Agent`，多模块共享。
4. **UI 只消费抽象**：页面只依赖 `IMemoryService`、`IOptionsMonitor<MemoryOptions>`、`IEmbeddingConnectionTester` 等。
5. **与 ACP 对齐的接入模型**：DI 注册 `ITool` + Extension/`HostedService` 自注册 Hook。

### 1.2 非目标

- 拆掉对 `Seeing.Agent` 的 ProjectReference（插件形态仍需引用核心契约；目标是**缩小实际引用面**，不是零引用）。
- 重写检索/存储内核（FileStore / Index / Graph 保持不动）。
- 改变 `memory.json` 文件落点（仍由 UnifiedConfig 适配，但对 Core 隐藏）。
- 本方案不强制所有宿主改为「仅 Plugins 加载」；**双路径兼容**：直连 `AddMemoryServices` 与 Plugins/`MemoryExtension` 均可完整工作且不双重注册。

### 1.3 成功标准

| 检查项 | 通过条件 |
|--------|----------|
| WebUI `Program.cs` | 无 `UseMemoryTools`、无手动 `Register(Chat/Tool/Recall Handler)` |
| `Core/**` | 无 `UnifiedConfigManager` / `ILlmService`；LLM 仅 `ITextCompletion` |
| 核心库 | 已注册 `IConfigSectionStore`、`ITextCompletion`、`IProviderEndpointLookup` |
| `Integration/**` | 允许适配 Agent；`MemoryExtension` 完整导出 Tools/Hooks |
| 任意宿主 | 仅 `AddMemoryServices()` 后，对话结束可入队、工具列表含 `memory_search` |
| 测试 | Memory.Tests 全绿；新增端口适配单元测试 |

---

## 二、目标架构

### 2.1 分层

```
┌─────────────────────────────────────────────────────────────┐
│ Host (WebUI / TUI / Generic Host)                           │
│  - services.AddMemoryServices()                             │
│  - UI: IMemoryService / IOptionsMonitor<MemoryOptions>      │
└───────────────────────────┬─────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────┐
│ Seeing.Agent（核心窄接口 — 方案 B）                           │
│  IConfigSectionStore  ← UnifiedConfigManager 实现           │
│  ITextCompletion      ← LlmService 适配实现                 │
│  IProviderEndpointLookup（可选）← Providers 字典             │
└───────────────────────────┬─────────────────────────────────┘
          ▲  Integration 依赖窄接口（及 Hook/ITool 契约）
┌─────────┴───────────────────────────────────────────────────┐
│ Integration/                                                │
│  MemoryExtension / BootstrapHostedService / MemorySearchTool│
│  MemoryOptionsStore(节名 "Memory") / Embedding 适配         │
└───────────────────────────┬─────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────┐
│ Core/  禁止 UnifiedConfigManager / ILlmService              │
│  可用: ITextCompletion 或薄包装；IOptions<MemoryOptions>     │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 依赖规则（硬约束）

| 层 | 允许依赖 | 禁止依赖 |
|----|----------|----------|
| `Core/` | `ITextCompletion`、`IOptions<MemoryOptions>`、`IProviderEndpointLookup`、MS.Extensions、Sqlite | `UnifiedConfigManager`、`ILlmService`、`ToolInvoker`、`ProviderConfig` |
| `Integration/` | Seeing.Agent（含 Hook/ITool）、Seeing.Session、Core | WebUI 类型 |
| WebUI | 抽象接口 + Options | Hook/Tool 装配、`MemoryOptionsProvider` 具体类型 |

### 2.3 与 ACP 对齐点

| ACP | Memory 对应 |
|-----|-------------|
| `AddSeeingAcp()` | `AddMemoryServices()` |
| `AddSingleton<ITool>(AcpTool)` | `AddSingleton<ITool>(MemorySearchTool)` |
| `AcpHookRegistrationHostedService` | `MemoryBootstrapHostedService` |
| `AcpExtension.GetTools()` | `MemoryExtension.GetTools()` + `GetHookHandlers()` |
| 宿主不手写 Register | 宿主不手写 Register |

---

## 三、核心窄接口（方案 B — 落在 Seeing.Agent）

新增文件建议：

```
src/Seeing.Agent/Configuration/IConfigSectionStore.cs
src/Seeing.Agent/Llm/ITextCompletion.cs
src/Seeing.Agent/Llm/IProviderEndpointLookup.cs   # 可选，供 Embedding
src/Seeing.Agent/Configuration/ProviderEndpoint.cs # 或 Llm/ 下简单 DTO
```

### 3.1 `IConfigSectionStore`

```csharp
namespace Seeing.Agent.Configuration;

public interface IConfigSectionStore
{
    T GetSection<T>(string sectionName) where T : class, new();
    Task SaveSectionAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) where T : class;
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged; // 或更窄的 SectionChanged
}
```

- **实现**：`UnifiedConfigManager` 显式实现该接口（已有 `GetSection` / `SaveSectionAsync` / `ConfigChanged`）。
- **DI**：`services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());`
- **Memory**：`IMemoryOptionsStore` 可为薄封装：`Get/Save` 固定 `sectionName: "Memory"`，便于 UI；Core 侧优先 `IOptionsMonitor<MemoryOptions>`（由 Store 驱动刷新）。

### 3.2 `ITextCompletion`

```csharp
namespace Seeing.Agent.Llm;

public interface ITextCompletion
{
    /// <summary>
    /// 非流式文本补全。model 为空时使用 DefaultModel。
    /// </summary>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        CancellationToken ct = default);
}
```

- **实现**：新建 `TextCompletionService : ITextCompletion`，内部调用 `ILlmService.CompleteAsync`，组装 `ChatRequest`，取出 `Message.Content`。
- **DI**：`AddSingleton<ITextCompletion, TextCompletionService>()`（在 `AddSeeingAgent` 中注册，保证 Memory 可用）。
- **Memory Core**：`LlmMemoryExtractor` / `LlmMemoryEvolution` **直接依赖 `ITextCompletion`**（允许 `using Seeing.Agent.Llm` 仅此窄接口），**禁止**注入 `ILlmService`。

> 说明：Core 仍引用 Seeing.Agent 程序集，但依赖面从「配置中枢 + 完整 LLM」收窄为两个接口。若日后要零引用 Agent，再把这两个接口挪到 Contracts 包（方案 C）。

### 3.3 `IProviderEndpointLookup`（Embedding 用）

```csharp
namespace Seeing.Agent.Llm;

public sealed class ProviderEndpoint
{
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
}

public interface IProviderEndpointLookup
{
    bool TryGet(string providerName, out ProviderEndpoint? endpoint);
}
```

- **实现**：读 `UnifiedConfigManager.SeeingAgent.Providers` 或 `IOptions<SeeingAgentOptions>`。
- Memory 的 `IProviderConfigAccessor` **删除或改为 typedef/弃用**，统一用核心接口，避免 Memory.Abstractions 引用 `ProviderConfig`。

### 3.4 Memory 层还要不要自有端口？

| 能力 | 决策 |
|------|------|
| LLM | **直接用 `ITextCompletion`**，不再强制 `IMemoryLlmClient`（可删，或 `IMemoryLlmClient` 继承/委托它仅为兼容） |
| 配置 | **用 `IConfigSectionStore` + 可选 `IMemoryOptionsStore` 薄封装** |
| Provider | **用 `IProviderEndpointLookup`** |
| 会话结束 | 仍用 Memory 自有 `IMemorySessionEvents`（Session 与 Agent 配置/LLM 不同域；或日后抽 `ISessionLifecycleEvents` 到核心，本期不做） |

---

### 3.5 会话事件与 Embedding（Memory 域）

```csharp
// Seeing.Agent.Memory.Abstractions — 本期仍留在 Memory
public interface IMemorySessionEvents
{
    IObservable<string> SessionEnded { get; }
}
```

- Integration：`SessionEventPublisherAdapter` 订阅 `ISessionEventPublisher`。
- Embedding：`IEmbeddingService` / `IEmbeddingConnectionTester` 依赖核心 `IProviderEndpointLookup`，不依赖 `UnifiedConfigManager`。

---

## 四、组件注册职责

### 4.1 `AddMemoryServices()`（唯一宿主入口）

职责：

1. 注册 Options / Store / 端口默认适配（若宿主已提供则 `TryAdd` 不覆盖）。
2. 注册 Core 服务与 HostedService（Pipeline / Evolution / Indexing）。
3. `AddSingleton<MemorySearchTool>()` + `AddSingleton<ITool>(sp => …)`。
4. `AddHostedService<MemoryBootstrapHostedService>()` —— 启动时幂等注册 Hook。
5. **不**要求宿主再调任何 `Use*`。

可选：`AddMemoryServices(Action<MemoryOptions>?)` 与现有一致。

### 4.2 `MemoryBootstrapHostedService`

```csharp
// 伪代码
protected override Task ExecuteAsync(ct)
{
    var hooks = sp.GetServices<IHookHandler>()
        .Where(h => h is ChatMemoryHandler or ToolMemoryHandler or MemoryRecallHandler);
    // 更好：显式解析三个 Handler 类型并 Register
    foreach (var h in handlers)
        if (!hookManager.IsRegistered(h)) // 或按 Spec+类型去重
            hookManager.Register(h);
    return Task.CompletedTask;
}
```

幂等策略（任选其一，方案指定）：

- **推荐**：`HookManager` 若无查询 API，则 Bootstrap 用静态 `Interlocked` / 单例内 `_registered` 标志，保证进程内只注册一次。
- Extension 路径若也会 `GetHookHandlers` → ExtensionManager 注册：则 **Bootstrap 与 Extension 二选一**，或 Bootstrap 检测「已由 Extension 注册」跳过。

**推荐最终策略**：

- **直连 `AddMemoryServices`**：仅 Bootstrap 注册 Hook；`ITool` 靠 DI。
- **Plugins 加载 `MemoryExtension`**：`GetHookHandlers`/`GetTools` 导出；`ConfigureServices` 调 `AddMemoryServices`；Bootstrap 发现 Hook 已存在则跳过（按 Handler 类型名记录）。

### 4.3 `MemoryExtension`（插件路径）

```csharp
ConfigureServices(services) => services.AddMemoryServices();

InitializeAsync:
  解析 MemorySearchTool、三个 HookHandler 到字段

GetTools() => yield MemorySearchTool as ITool
GetHookHandlers() => yield 三个 Handler

DisposeAsync: 可选 Unregister（若 HookManager 支持；暂不支持则文档说明进程级残留可接受）
```

### 4.4 删除

- `MemoryToolRegistrationExtensions.UseMemoryTools`
- WebUI `Program.cs` 中 Hook 注册与 `UseMemoryTools`
- 设置页对 `MemoryOptionsProvider` 的直接注入

---

## 五、工具形态

将 `MemorySearchTool` 从注解静态/实例方法改为 **`ToolBase`（ITool）**，与 `AcpTool` 一致：

```csharp
public sealed class MemorySearchTool : ToolBase
{
    public override string Id => "memory_search";
    // ParametersSchema + ExecuteAsync → IMemoryRecallService
}
```

注册：

```csharp
services.AddSingleton<MemorySearchTool>();
services.AddSingleton<ITool>(sp => sp.GetRequiredService<MemorySearchTool>());
```

`ToolInvoker` 构建时自动收录，**无需** `RegisterToolsFromType`。

---

## 六、WebUI 改造清单

| 文件 | 改动 |
|------|------|
| `Program.cs` | 仅保留 `AddMemoryServices()`；删除 Hook Register 与 `UseMemoryTools` |
| `MemorySettingsPage.razor` | `@inject IOptionsMonitor<MemoryOptions>` + `IMemoryOptionsStore`（或仅 Store）+ `IEmbeddingConnectionTester`；保存走 Store |
| 浏览/图谱/详情页 | 保持 `IMemoryService`/`IMemoryGraph`（已合规） |

宿主示例（目标态）：

```csharp
builder.Services.AddSeeingAgent(...);
builder.Services.AddMemoryServices(); // 完整能力
// InitializeSeeingAgentAsync() 可选加载 Plugins 中的 MemoryExtension（若配置了）
```

---

## 七、文件级变更地图

### 7.1 新增（核心）

```
src/Seeing.Agent/Configuration/IConfigSectionStore.cs
src/Seeing.Agent/Llm/ITextCompletion.cs
src/Seeing.Agent/Llm/TextCompletionService.cs
src/Seeing.Agent/Llm/IProviderEndpointLookup.cs
src/Seeing.Agent/Llm/ProviderEndpoint.cs
src/Seeing.Agent/Llm/OptionsProviderEndpointLookup.cs  # 或 Configuration 下
```

### 7.2 新增（Memory）

```
Abstractions/IMemorySessionEvents.cs
Abstractions/IMemoryOptionsStore.cs          # 薄封装，可选
Integration/Hosting/MemoryBootstrapHostedService.cs
Integration/Tools/MemorySearchTool.cs       # ToolBase
Integration/Adapters/SessionEventPublisherAdapter.cs
```

### 7.3 修改

```
UnifiedConfigManager.cs                      # : IConfigSectionStore
ServiceCollectionExtensions.cs               # 注册窄接口
Core/Evolution/*                             # → ITextCompletion
Embedding/*                                  # → IProviderEndpointLookup
Configuration/MemoryOptionsProvider.cs       # → IMemoryOptionsStore / IConfigSectionStore
MemoryExtension.cs / MemoryServiceExtensions.cs / WebUI Program.cs
```

### 7.4 删除

```
Extensions/MemoryToolRegistrationExtensions.cs
Integration/MemoryTools.cs
Abstractions/IProviderConfigAccessor.cs       # 改用核心 IProviderEndpointLookup
```

### 7.5 测试

```
Seeing.Agent.Tests: TextCompletionServiceTests, ConfigSectionStore 注册冒烟
Memory.Tests: Extractor mock ITextCompletion；Embedding mock IProviderEndpointLookup
```

---

## 八、实施阶段

### Phase D0 — 核心窄接口（方案 B，0.5–1d）

- 新增并实现 `IConfigSectionStore`、`ITextCompletion`、`IProviderEndpointLookup` + `ProviderEndpoint`。
- `AddSeeingAgent` 注册上述接口。
- 单测：`TextCompletionService` mock `ILlmService`；ConfigStore 用内存假实现或现有 manager。

### Phase D1 — Memory Core 改依赖窄接口（1d）

- Extractor / Evolution → `ITextCompletion`。
- Options / Embedding → `IConfigSectionStore` / `IProviderEndpointLookup`。
- EvolutionWorker → `IMemorySessionEvents`。
- 删除对 `UnifiedConfigManager`、`ILlmService`、`ProviderConfig` 的 Core/Abstractions 引用。
- 单测全绿。

### Phase D2 — Integration + ITool + Bootstrap（1–2d）

- `IMemoryOptionsStore` 薄封装（可选）+ Settings 绑定。
- `MemorySearchTool : ToolBase` + DI `ITool`。
- `MemoryBootstrapHostedService`；删除 `UseMemoryTools`。
- 补齐 `MemoryExtension`。

### Phase D3 — 宿主瘦身（0.5d）

- WebUI `Program.cs` / Settings 页改造。
- 手动验证：启动 → 对话 → 入队；工具列表有 `memory_search`；设置页保存 `memory.json`。

### Phase D4 — 加固（0.5d）

- 双重注册防护测试。
- README / 本方案状态更新为「已实施」。
- （可选）Arch 测试防止 Core 回流依赖。

---

## 九、风险与缓解

| 风险 | 缓解 |
|------|------|
| Hook 双重注册（Bootstrap + Extension） | 幂等标志；Extension 加载时跳过 Bootstrap 或相反 |
| `ITool` 与旧注解工具 ID 冲突 | 统一 ID `memory_search`；删除注解路径 |
| 无 `IHttpClientFactory` 的宿主 | `AddMemoryServices` 内 `services.AddHttpClient()` 或 `TryAddSingleton<HttpClient>` |
| 无 Session 事件的宿主 | `NullMemorySessionEvents`；仅空闲演化 |
| Settings 页保存路径变化 | Store 仍写 `memory.json`，行为不变 |

---

## 十、目标态宿主对照

### 改造前（WebUI）

```csharp
builder.Services.AddMemoryServices();
// …
hookManager.Register(chatHandler);
hookManager.Register(toolHandler);
hookManager.Register(recallHandler);
sp.UseMemoryTools();
```

### 改造后（WebUI）

```csharp
builder.Services.AddMemoryServices();
// InitializeSeeingAgentAsync() 如常
// Memory 的 Tool/Hook 已自注册
```

### 纯插件加载（可选）

```json
// seeing.json
"Plugins": [ { "Spec": "file://.../Seeing.Agent.Memory.dll" } ]
```

`MemoryExtension.ConfigureServices` → `AddMemoryServices()`；Tools/Hooks 经 ExtensionManager 注册。

---

## 十一、决策摘要

| 决策 | 选择 | 理由 |
|------|------|------|
| 公共抽象策略 | **B：核心库窄接口** | 多模块复用；不新建包 |
| 配置抽象 | `IConfigSectionStore` | 替代直接依赖 `UnifiedConfigManager` |
| LLM 抽象 | `ITextCompletion` | 替代完整 `ILlmService` |
| Provider | `IProviderEndpointLookup` | Embedding 不绑 `ProviderConfig` 类型 |
| 主注册路径 | `AddMemoryServices` + `ITool` + Bootstrap | 对齐 ACP |
| Extension | 补齐并兼容 Plugins | 独立 DLL |
| 独立 Contracts 包 | 暂缓 | 有跨宿主需求再升 C |

## 十二、审阅结论

已确认：方案 **B**。审阅问题默认：

1. 双路径保留（`AddMemoryServices` + Extension）。  
2. Hook 幂等先在 Memory 侧做。  
3. Settings 用 `IMemoryOptionsStore`（基于 `IConfigSectionStore`）。

下一步：编写实施计划并开工（D0 核心窄接口 → D1 Memory Core → D2/D3 宿主）。
