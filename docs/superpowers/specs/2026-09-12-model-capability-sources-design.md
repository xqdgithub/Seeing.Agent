# 模型能力源（多模块、可编排）设计

**日期:** 2026-09-12  
**状态:** 已实现（架构终审 PASS_WITH_MINOR，多余包引用已去掉）
**说明:** 能力数据统一走多源体系；OpenCodeZen 内嵌预置思路废弃。

## 1. 问题

LLM Provider 的 List Models 常只返回 `id`，limit / thinking / modalities / pricing 等能力依赖各插件内静态表（DeepSeek、OpenCodeZen 各自维护），导致：

- 能力数据散落、易漂移
- 配置型 Provider（`seeing.json` Models）难以统一补全
- 无法在系统级开关、按 Provider 作用域、多数据源协作与调序
- UI 无法按「能力源模块」配置与调整优先级

## 2. 目标

1. **Abstractions 定义端口**：按 `providerId + modelId` 查找能力；字段 **FillEmpty** 合并；别名解析；**列表/编辑/刷新** 由源接口承接，UI 只委托不直写文件。
2. **多模块共同作用**：每个能力源是独立 `ISeeingModule`（Activate 时登记 `IModelCapabilitySource`），**自管存储与源级配置**。
3. **系统级编排配置**：总开关、Provider 作用域、各源 `Enabled` + `Order`（**数值越小越先**）。
4. **合并语义**：高优先级（小 Order）先 FillEmpty；低优先级只补仍空字段。
5. **Provider 层调用**：`GetModelsAsync` 末尾 `await manager.TryEnrichIfEnabledAsync(...)`；不调用即完全不用。`ModelConfigManager` **不** enrich。
6. **变更传播**：CLR 事件供 UI 即时刷新；副作用经 `IReloadSignalBus` 发布 `ModelCapabilitiesChange`，由 Core 内 `IReloadHandler` 订阅。
7. **配置页**：按源模块展示 / 配置 / 拖拽调序；钻取后经编辑接口改条目与别名。

## 3. 非目标

- Core / `ModelConfigManager` 静默改写目录
- 改变思考出站协议（沿用现有 `ThinkingOptions` + Client）
- 免费/付费业务判定进能力源（仍由 Provider）
- 本期做齐所有远程目录厂商；首期一个 models.dev 系源即可
- 强制所有 Provider **模块**依赖本能力（可选模块）；但一旦调用 enrich，必须走 `IModelCapabilityManager`（Null 实现在 Core，真实现由能力模块 Replace）

## 4. 分层与包

| 层 | 包 / 位置 | 职责 |
|----|-----------|------|
| Abstractions | `Seeing.Agent.Abstractions`（Llm 区） | DTO、事件、端口接口、`ModelCapabilitiesChange`、`ModelCapabilityFillEmpty` 纯函数（**唯一** FillEmpty 实现）。**不含**可运行 Manager 实现（零实现纪律） |
| Spine | `Seeing.Agent.Core` | `NullModelCapabilityManager` + `TryAddSingleton<IModelCapabilityManager, NullModelCapabilityManager>()`；`ModelCapabilityCatalogReloadHandler`（只调 `IModelConfigManager.RefreshCatalogAsync`）；`ConfiguredLlmProvider` / `ProviderManager` **必选注入** Manager。**零** ProjectReference → `Llm.ModelCapabilities` |
| Capabilities | `Seeing.Agent.Llm.ModelCapabilities` | 真 Manager + Registry + 系统 Options（`IConfigSectionRegistry.Register("ModelCapabilities", …)`）；Manager **只调用** Abstractions 的 `ModelCapabilityFillEmpty`，不另起合并逻辑；模块 id `llm.modelcapabilities`；Replace Null |
| Capabilities | `Seeing.Agent.Llm.ModelCatalog.ModelsDev` | 首个 Source（List+Edit+Refresh）；模块 id `llm.modelcatalog.modelsdev`；`DependsOn: ["llm.modelcapabilities"]` |
| Plugs | DeepSeek / OpenCodeZen | 注入 `IModelCapabilityManager`；逐步删除重复静态表 |
| WebUI | Settings | 经 Manager / Edit 接口；不直写存储 |

**依赖硬规则：**

- Core → 仅 Abstractions 的 `IModelCapabilityManager`（Null 或被能力模块替换的实现）
- 能力编排包 / Source 包 → Abstractions；**禁止**引用 Core
- Catalog ReloadHandler **只放 Core**；禁止能力包调用 `ModelConfigManager` / `EnqueueRefresh`
- Provider / UI → Abstractions（Manager）；编辑时 `manager.GetSource` + `is IEditable…`

### 4.1 术语

| 名称 | 含义 |
|------|------|
| `Module.Id` | 如 `llm.modelcatalog.modelsdev`（结算 / DependsOn） |
| `Source.Id` | 如 `modelsdev`（配置 `Sources[].Id`、UI、事件） |
| 配置与 UI **一律用 Source.Id**；勿与 Module.Id 混用 |

## 5. 数据模型

### 5.1 `ModelCapabilityEntry`

与 `ModelConfig` 可对齐的子集（补全用）：

| 字段 | 说明 |
|------|------|
| `ProviderId` | 可空；空表示跨 Provider 通用条目 |
| `ModelId` | 规范 id |
| `Name` | 可选显示名 |
| `Limit` | Context / Output（可空字段见 FillEmpty） |
| `Modalities` | 可选 |
| `Options.Thinking` | 与 `ModelConfig.Options.Thinking` 对齐；Entry **不**另设顶层 Thinking 字段 |
| `Pricing` | 可选 |

**不**在 Entry 上放 `Aliases`。别名唯一真相源见 §5.2。

### 5.2 别名（单一真相源）

- 仅顶层表：`ModelCapabilityAlias`（`ProviderId?` + `FromModelId` → `ToModelId`）
- `ResolveAlias` / `ListAliasesAsync` / `UpsertAliasAsync` **只**读写该表
- 禁止 Entry 内嵌别名与顶层表双权威

### 5.3 FillEmpty 规则

| 字段 | 视为「空」可填 |
|------|----------------|
| `Name` | null / whitespace |
| `Limit.Context` / `Output` | **仅当**对应可空包装为 null，或使用 `ModelCapabilityLimit?` / 明确「未设置」语义；**禁止**把显式 `4096` 当成空（避免与「永不覆盖明确值」冲突） |
| `Modalities` | null，或 Input/Output 皆空 |
| `Options.Thinking` | null，或 `Supported!=true` 或 `Levels` 空 |
| `Pricing` | null |

实现注意：若 `ModelConfig.Limit` 目前是非可空 struct，FillEmpty 侧用「是否仍为类型默认且从未被 Provider/用户写出」需在实现计划用辅助标记或与反序列化缺省对齐；规格要求 **显式写入的 4096 不得被覆盖**。

## 6. 接口

命名：`Registry` = 注册集合；`Manager` = 系统编排与**唯一应用入口**。

### 6.1 核心源：`IModelCapabilitySource`

```csharp
public interface IModelCapabilitySource
{
    string Id { get; }
    string DisplayName { get; }

    ModelCapabilitySourceStatus GetStatus();

    ValueTask<ModelCapabilityEntry?> TryGetAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);

    string ResolveAlias(string providerId, string modelId);

    event EventHandler<ModelCapabilitySourceChangedEventArgs>? Changed;
}

public sealed class ModelCapabilitySourceStatus
{
    public DateTimeOffset? LastLoadedAt { get; init; }
    public int EntryCount { get; init; }
    public int AliasCount { get; init; }
    public string? LastError { get; init; }
    // 不设 Supports*；列表/编辑/刷新能力由 is IListable/IEditable/IRefreshable 判定
}
```

```csharp
public enum ModelCapabilitySourceChangeKind
{
    Reloaded,
    EntriesEdited,
    AliasesEdited,
    RefreshFailed,
    LoadFailed
}

public sealed class ModelCapabilitySourceChangedEventArgs : EventArgs
{
    public required string SourceId { get; init; }
    public ModelCapabilitySourceChangeKind Kind { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
```

单源查找顺序：`ResolveAlias` → 精确 provider+model → 通用（空 provider）+model → 同 modelId 跨 Provider 回退（默认开，源可关）。

### 6.2 可选能力（接口隔离）

```csharp
public interface IListableModelCapabilitySource : IModelCapabilitySource
{
    ValueTask<IReadOnlyList<ModelCapabilityEntry>> ListEntriesAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<ModelCapabilityAlias>> ListAliasesAsync(CancellationToken ct = default);
}

public sealed class ModelCapabilityAlias
{
    public string? ProviderId { get; init; }
    public required string FromModelId { get; init; }
    public required string ToModelId { get; init; }
}

public interface IEditableModelCapabilitySource : IListableModelCapabilitySource
{
    ValueTask UpsertEntryAsync(ModelCapabilityEntry entry, CancellationToken ct = default);
    ValueTask DeleteEntryAsync(string? providerId, string modelId, CancellationToken ct = default);
    ValueTask UpsertAliasAsync(ModelCapabilityAlias alias, CancellationToken ct = default);
    ValueTask DeleteAliasAsync(string? providerId, string fromModelId, CancellationToken ct = default);
}

/// <summary>批量保存（一次 .bk + 一次写盘）。与逐条 Upsert 分离，避免「可选却必选」矛盾。</summary>
public interface IBatchEditableModelCapabilitySource : IEditableModelCapabilitySource
{
    ValueTask ReplaceAllAsync(
        IReadOnlyList<ModelCapabilityEntry> entries,
        IReadOnlyList<ModelCapabilityAlias> aliases,
        CancellationToken ct = default);
}

public interface IRefreshableModelCapabilitySource : IModelCapabilitySource
{
    ValueTask RefreshAsync(CancellationToken ct = default);
}
```

编辑委托路径：

```
UI → manager.GetSource(id) → as IEditable… → Upsert*/Delete*
  → Source 写盘/.bk → Changed → Manager.CapabilitiesChanged + Publish(ModelCapabilitiesChange)
```

### 6.3 Registry（仅模块 Activate；非应用 API）

```csharp
public interface IModelCapabilitySourceRegistry
{
    void Register(IModelCapabilitySource source);
    void Unregister(string sourceId);
    IReadOnlyList<IModelCapabilitySource> GetSources();

    event EventHandler? SourcesChanged;
}
```

**无**对外 `GetSource`。**禁止** UI / Provider 注入本 Registry；仅能力模块 Activate/Deactivate 与 Manager 实现使用。应用与 UI 只经 Manager。

### 6.4 Manager（唯一应用入口）

```csharp
public interface IModelCapabilityManager
{
    ValueTask<ModelConfig> TryEnrichIfEnabledAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ModelCapabilitySourceRegistration> GetRegistrations();

    IModelCapabilitySource? GetSource(string sourceId);

    event EventHandler<ModelCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    /// <summary>
    /// 统一变更出口：抬升 CapabilitiesChanged，并 Publish ModelCapabilitiesChange。
    /// invalidateModelCatalog 写入信号字段（单次覆盖）；默认取自 Options.InvalidateModelCatalogOnChange。
    /// </summary>
    ValueTask NotifyChangedAsync(
        ModelCapabilitiesChangeReason reason,
        IReadOnlyList<string>? affectedSourceIds = null,
        ModelCapabilitySourceChangeKind? sourceKind = null,
        bool? invalidateModelCatalog = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Core 内 Null 实现：enrich 原样返回；无 registrations；Notify 为 no-op。</summary>
public sealed class NullModelCapabilityManager : IModelCapabilityManager { /* in Core */ }

public sealed class ModelCapabilitySourceRegistration
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public int Order { get; init; }
    public bool Enabled { get; init; }
    public bool Registered { get; init; }
    public ModelCapabilitySourceStatus? Status { get; init; }
    public bool CanList { get; init; }   // Manager 投影：source is IListable…
    public bool CanEdit { get; init; }
    public bool CanRefresh { get; init; }
    public bool CanBatchEdit { get; init; }
}

public enum ModelCapabilitiesChangeReason
{
    SystemConfigChanged,
    SourcesReorderedOrToggled,
    SourceRegistered,
    SourceUnregistered,
    SourceDataChanged
}

public sealed class ModelCapabilitiesChangedEventArgs : EventArgs
{
    public ModelCapabilitiesChangeReason Reason { get; init; }
    public IReadOnlyList<string> AffectedSourceIds { get; init; } = [];
    public ModelCapabilitySourceChangeKind? SourceKind { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
```

### 6.5 事件与 `IReloadSignalBus`

| 通道 | 用途 |
|------|------|
| CLR `Changed` / `CapabilitiesChanged` | UI 即时刷新 |
| `PublishAsync(ModelCapabilitiesChange)` | 副作用编排 |

```csharp
public sealed class ModelCapabilitiesChange : IReloadSignal
{
    public ModelCapabilitiesChangeReason Reason { get; init; }
    public IReadOnlyList<string> AffectedSourceIds { get; init; } = [];
    public ModelCapabilitySourceChangeKind? SourceKind { get; init; }
    /// <summary>SSOT：是否刷聚合目录。Handler 只读本字段，禁止再读 Options。</summary>
    public bool InvalidateModelCatalog { get; init; }
}
```

**Publish：** Manager 在 Source.Changed / 配置热更 / Register·Unregister 后调用 **`NotifyChangedAsync`**（唯一出口）。`invalidateModelCatalog` 参数：null → 用 Options 默认；非 null → 单次覆盖。Handler **只读**信号字段。

设置页勾选「同时刷新模型目录」→ `NotifyChangedAsync(..., invalidateModelCatalog: true)`。

**Subscribe（仅 Core）：**

```text
ModelCapabilityCatalogReloadHandler : ReloadHandlerBase<ModelCapabilitiesChange>
  → 仅当 signal.InvalidateModelCatalog → IModelConfigManager.RefreshCatalogAsync(...)
```

禁止能力包引用 `ModelConfigManager` / `EnqueueRefresh`。可选：各 plug 自注册 Handler 清本 Provider TTL（只依赖 Abstractions 信号 + 本包缓存）。

## 7. 系统配置 `ModelCapabilities`

由 `Seeing.Agent.Llm.ModelCapabilities` 模块经 `IConfigSectionRegistry.Register("ModelCapabilities", …)` 自注册（禁止旁路读 json）。

```json
{
  "ModelCapabilities": {
    "Enabled": true,
    "Providers": ["*"],
    "InvalidateModelCatalogOnChange": false,
    "Sources": [
      { "Id": "modelsdev", "Order": 0, "Enabled": true }
    ]
  }
}
```

| 字段 | 含义 |
|------|------|
| `Enabled` | 总开关 |
| `Providers` | `["*"]` 或 ProviderId 白名单 |
| `Sources[]` | Source.Id + Order（越小越先）+ Enabled |
| `InvalidateModelCatalogOnChange` | **仅**供 Manager 计算发布信号时的默认值；Handler 不读 |

未出现在 `Sources` 的已注册源：Activate 时视为 Enabled=true、Order=max+10 追加。

源专属配置（RemoteUrl 等）由各 Source 模块自管 ConfigSection。

## 8. 首个 Source：`modelsdev`

- Module.Id = `llm.modelcatalog.modelsdev`；Source.Id = `modelsdev`
- 存储自管：`~/.seeing/model-capabilities/modelsdev/catalog.json` + `catalog.json.bk`
- 启动：本地优先，否则嵌入快照写入本地
- 实现 `IListable` + `IEditable` + `IBatchEditable` + `IRefreshable`
- 编辑/刷新必须经接口；成功抬 `Changed`

### 与 Provider

| 组件 | 行为 |
|------|------|
| OpenCodeZen / DeepSeek | DI 注入 Manager；List 后 `await TryEnrichIfEnabledAsync`；删重复静态能力 |
| ConfiguredLlmProvider | **Core 内**构造时由 `ProviderManager` 传入 `IModelCapabilityManager`（Null 或真实现）；同样 await enrich。Core **不**引用能力包 |

## 9. Enrich 流程

```
Provider.GetModelsAsync
  → models
  → foreach m: m = await manager.TryEnrichIfEnabledAsync(m, ct)
       → if !Enabled: return m
       → if Providers 非 * 且 m.Provider 不在名单: return m
       → foreach source in enabled Sources by Order ASC:
            entry = await source.TryGetAsync(...)
            if entry != null: m = FillEmpty(m, entry)
       → return m
```

Activate 完成前 Source 须完成本地/嵌入加载；未就绪时 `TryGetAsync` 返回 null（不阻塞、不 `.Result`）。

## 10. WebUI

1. 总开关、Providers、源列表（Order 拖拽）  
2. 钻取：`GetSource` → `is IListable/IEditable/IRefreshable/IBatchEditable`  
3. 订阅 `CapabilitiesChanged`  
4. 保存时可勾选「同时刷新模型目录」→ `NotifyChangedAsync(..., invalidateModelCatalog: true)`

## 11. 模块生命周期与 Null

```
AddSeeingCore
  → TryAddSingleton<IModelCapabilityManager, NullModelCapabilityManager>
  → 登记 ModelCapabilityCatalogReloadHandler

AddSeeingModule<ModelCapabilitiesModule>  // Replace 真 Manager + Registry
AddSeeingModule<ModelsDevCatalogModule>
AddSeeingModule<OpenCodeZenLlmModule>

Activate:
  ModelCapabilities → 订阅 Registry/Options；接 IReloadSignalBus
  ModelsDev → Load → Registry.Register
  Providers → Register
```

未加能力模块：全体 Provider 仍拿到 Null Manager，enrich 恒 no-op。

## 12. 测试

| 区域 | 用例 |
|------|------|
| FillEmpty | 空补全；显式 4096 不被盖；已有 Thinking 不盖 |
| Manager 异步 | 多源 Order；Enabled/Providers 门闸；禁止同步阻塞 |
| Null | 无能力模块时 enrich 原样 |
| Reload | Publish 信号位；Handler 只看 `InvalidateModelCatalog`；Options 不直接被 Handler 读 |
| Edit | 接口写盘 + `.bk`；无文件 API |
| Alias | 仅顶层表 |
| Layer | 能力包无 Core 引用；Handler 在 Core |

## 13. 风险与边界

- models.dev schema 变更：映射隔离；失败保留旧文件 + `.bk`
- 跨 Provider modelId 回退可能误补：源级可关
- `ModelConfig.Limit` 非可空与「未设置」语义：实现计划用可空中间模型或显式标志解决

## 14. 验收

- ≥2 Fake Source，Order 级联正确  
- Zen 免费推理模型在开启且快照含数据时 Picker 显示思考档  
- 仅 Null Manager / Enabled=false → 与今日一致  
- Edit/Refresh → `.bk` + CapabilitiesChanged +（可选）Reload 刷目录  
- Core 工程无对 `Llm.ModelCapabilities` 的 ProjectReference  

## 15. 实现分期

| 阶段 | 内容 |
|------|------|
| P0 | Abstractions 端口/事件/FillEmpty 纯函数 + Core Null Manager + Catalog ReloadHandler + Configured 注入点 + 单测 |

| P1 | ModelCapabilities 真 Manager + ModelsDev Source（List/Edit/Batch/Refresh + `.bk`） |
| P2 | ProviderManager/Configured/Zen/DeepSeek 接入异步 enrich；删重复静态表 |
| P3 | WebUI 调序 + 编辑委托 + 单次 Invalidate 勾选 |
