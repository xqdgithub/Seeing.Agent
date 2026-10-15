# 模型选择（启用清单）设计

> **日期：** 2026-10-13
> **状态：** 待评审
> **范围：** `IModelConfigManager` 目录过滤 + `ModelSelection` 配置节 + ModelsPage 勾选 UI + 模型默认 limit 调整

## 1. 背景与问题

Provider（配置型 `ProviderConfig` 与扩展型 `ILlmProvider`）的 `GetModelsAsync` 返回该连接下**全部**可用模型，`ModelConfigManager` 将它们聚合为目录，WebUI 各处选择器直接展示全量。

用户实际只希望使用其中一部分模型。现状缺少「从全部可用模型中挑选出想用子集」的机制：选择器被大量不关心的模型淹没。

**目标：** 在核心服务统一提供「启用清单（allowlist）」，Provider 仍返回全量，用户勾选出想用的子集，所有消费端（WebUI / TUI / CLI / Gateway）自动只看到启用子集。

**非目标：**
- 不改变 `ILlmProvider.GetModelsAsync` 契约（Provider 永远返回全量）。
- 不拦截/禁用被隐藏模型的使用（隐藏仅影响可见性）。
- 不做 Provider 整体启用/禁用（那是另一维度）。

## 2. 需求决策（已确认）

| 项 | 决策 |
|----|------|
| 选择语义 | 从全部中挑选常用子集（启用清单 / allowlist） |
| 默认可见性 | 默认全部隐藏；勾选后才可见 |
| 空清单语义 | 空清单 = 该 Provider 全部隐藏（不区分「未配置」） |
| 存储位置 | 顶层统一配置节，覆盖配置型与扩展型 Provider |
| 配置级别 | **User 级**（与 `Providers` 一致，跨项目共享启用清单） |
| 过滤落点 | 核心服务（`ModelConfigManager`）统一过滤，另开全量入口 |
| 隐藏模型引用 | 不拦截，仍可解析、仍可执行 |
| 兼容性 | 项目处于开发阶段，不考虑向后兼容 |
| 模型列表默认视图 | 默认展示已启用；提供「显示全部」开关 |
| 添加模型交互 | 弹窗内两种模式切换：从列表选已有 / 手动新建 |
| context/output 默认 | 默认值改为 200k / 64k；缺失时也回落 200k / 64k |
| 出站 maxTokens | 请求未指定时回落到模型 `Limit.Output`（已有机制），兜底改为 64k |

## 3. 数据模型与存储

### 3.1 新配置节 `ModelSelection`

`seeing.json` 顶层，`ConfigScope.Both`，按 providerId 分组存启用模型 ID 列表：

```json
"ModelSelection": {
  "openai": ["gpt-4o", "o1"],
  "opencode-zen": ["big-pickle"]
}
```

- 值为**模型 ID**（`ModelConfig.Id`），与 Provider 下的模型标识对齐。
- Provider 键缺失或列表为空 → 该 Provider 全部隐藏。
- 同时覆盖配置型与扩展型 Provider（顶层节，不受 `RequireWritableProvider` 的扩展 Provider 限制，见 `src/spine/Seeing.Agent.Core/Llm/ModelConfigManager.cs:724`）。

### 3.2 DTO

新增 `ModelSelectionConfig`（放 `src/abstractions/Seeing.Agent.Abstractions/Llm/`）：

```csharp
public class ModelSelectionConfig
{
    [JsonPropertyName("providers")]
    public Dictionary<string, List<string>> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
```

在 `ConfigSectionRegistry.RegisterSpineSections()`（`src/spine/Seeing.Agent.Core/Configuration/ConfigSectionRegistry.cs:40`）注册：

```csharp
Register(new("ModelSelection", "seeing.json", ConfigScope.Both, typeof(ModelSelectionConfig)));
```

## 4. 目录管理器：双缓存与接口

### 4.1 缓存

`ModelConfigManager` 维护两份：

- `_modelCache`：全量目录（Provider 聚合结果，语义不变）。
- `_enabledCache`（新增）：全量按 `ModelSelection` 白名单过滤后的视图。
- `_providerIndex`：改为基于**启用视图**构建（供 `GetModelsByProvider`）。
- 新增 `_catalogProviderIndex`：基于**全量**构建（供新的全量查询入口）。

### 4.2 接口语义（`IModelConfigManager`）

| 方法 | 变更后语义 |
|------|-----------|
| `GetModels()` | 返回**启用子集** |
| `GetModelsByType(type, providerId)` | 返回**启用子集** |
| `GetModelsByProvider(providerId)` | 返回**启用子集** |
| `GetModel(id)` | 保持**全量**（隐藏模型仍可解析执行） |
| `GetCatalogModels()` **(新增)** | 返回全量，供管理 UI 勾选 |
| `GetCatalogModelsByProvider(providerId)` **(新增)** | 返回某 Provider 的全量 |
| `GetEnabledModelIds(providerId)` **(新增)** | 返回某 Provider 白名单 ID 列表（可能为空） |
| `SetEnabledModelsAsync(providerId, ids, ct)` **(新增)** | 写 `ModelSelection` 节 + 重建启用视图 |

### 4.3 过滤规则

- Provider 刷新构建全量后，立即应用白名单生成 `_enabledCache`。
- 白名单键：`ModelSelection.Providers[providerId]`；不存在 → 空列表 → 该 Provider 无启用模型。
- 匹配：`ModelConfig.Id`（大小写不敏感）∈ 白名单。
- `SetEnabledModelsAsync` 只重建视图 + 触发 `ModelConfigChanged`，**不重拉 Provider**。

### 4.4 门面与内部查找

- `IModelManager` / `ModelManager` 透传新增与变更方法。
- `ModelManager.NormalizeCatalogRef`（`src/spine/Seeing.Agent.Core/Llm/ModelManager.cs:234`）现用 `GetModels()` 找 key，需改用 `GetCatalogModels()`，否则隐藏模型无法规范化。

## 5. 持久化与热重载

### 5.1 持久化

`SetEnabledModelsAsync(providerId, ids)`：
1. 读当前 `ModelSelection` 节。
2. 更新 `Providers[providerId] = ids`；`ids` 为空则移除该键。
3. `SaveSectionAsync("ModelSelection", config, ConfigLevel.User, ct)`。

`ModelSelection` 声明 `ConfigScope.Both`，实际写入 **User 级**（`~/.seeing/seeing.json`），与 `Providers` 一致，跨项目共享。

### 5.2 热重载

`ModelReloadHandler`（`src/spine/Seeing.Agent.Core/Llm/ModelReloadHandler.cs:19`）扩展：
- `change.ChangedSections` 含 `ModelSelection` → 调 `_manager.RebuildEnabledView()`（仅重建 `_enabledCache`，不重拉 Provider）。
- 现有 `Providers` 分支逻辑不变。

### 5.3 并发与启动

- `_enabledCache` 重建在 `_cacheLock` 内完成，与全量刷新互斥。
- 白名单读取经 `UnifiedConfigManager.GetSection<ModelSelectionConfig>("ModelSelection")` 同步内存快照，与 `GetUserProviders()` 同模式。
- 构造时 `SeedConfiguredModels()` 之后立即应用一次白名单，保证初始视图正确。

## 6. UI：ModelsPage

### 6.1 模型列表（默认已启用 + 显示全部开关）

- 顶部新增开关「显示全部模型」：
  - **关（默认）**：列表数据源 = `GetModelsByProvider(providerId)`（启用子集）。
  - **开**：列表数据源 = `GetCatalogModelsByProvider(providerId)`（全量）。
- 新增「启用」列：`Checkbox`，`Checked` = 模型 ID ∈ 白名单。
  - 勾选/取消 → `SetEnabledModelsAsync(providerId, 更新后ID列表)` → 重载行。
- 顶部「全部启用 / 全部禁用」快捷按钮（作用于当前 Provider 全量）。
- 测试连接下拉保持全量（`GetCatalogModelsByProvider`），便于勾选前验证。

### 6.2 添加模型弹窗（两模式切换）

`Modal` 内用 `Radio` / `Tabs` 切换两种模式：

- **从列表选**：`ModelSelectDropdown`（`FilterType=null`、`ProviderId=当前`、数据源为全量未启用模型）；选中后预填 ID / 名称 / limit。
- **手动新建**：现有表单字段（ID、名称、用途类型、上下文、输出）。

保存逻辑不变（`AddModelAsync`）；新增模型后默认自动加入启用清单。

### 6.3 消费端

无需改动（自动只显示启用子集）：
- `ModelSelectBadge` / `ModelSelectDropdown` / `ModelSelectModal`（走 `GetModels()`）。
- `MainLayout.razor:127`（`AppState.AvailableModels`）、`MemorySettingsPage`、`Index.razor`、Tui `TuiCommandRouter`（走 `GetModelsByType`）。

必须适配：

| 位置 | 问题 | 改法 |
|------|------|------|
| `SessionWindow.razor:1368` | 发送前用 `GetModelsByType(Text).ContainsKey` 判存在，隐藏的默认模型会误判 | 改用全量存在性判断（`GetCatalogModels`） |
| `ModelManager.NormalizeCatalogRef`（`ModelManager.cs:234`） | 用 `GetModels()` 找 key，隐藏模型无法规范化 | 改用 `GetCatalogModels()` |

## 7. 模型 limit 默认值调整

### 7.1 类型默认值

`ModelLimits`（`src/abstractions/Seeing.Agent.Abstractions/Llm/ModelConfig.cs:138`）：

```csharp
public int Context { get; set; } = 200000;
public int Output  { get; set; } = 65536;
```

### 7.2 缺失回落

采用独立常量表达「回落默认值」：

```csharp
public static class ModelLimitDefaults
{
    public const int Context = 200000;
    public const int Output  = 65536;
}
```

`ModelCapabilityFillEmpty.DefaultLimitUnset`（`ModelCapabilityFillEmpty.cs:11`）语义为「FillEmpty 视为未设置的哨兵值」，与「默认 limit」是两件事：该哨兵值应与 `ModelLimits` 类型默认值保持同步（即改为 `200000`），使 `FillEmpty` 仍能识别「用户未显式设置」。

### 7.3 出站请求 maxTokens

`LlmService.PrepareClientRequest`（`LlmService.cs:149-158`）**已有**逻辑：请求未指定 `MaxTokens` 且模型 `Limit.Output > 0` 时，用 `modelConfig.Limit.Output` 回填。因此模型 limit 调整后自动生效。

仅需处理兜底：`LlmService.cs:346`、`:475` 的 `request.MaxTokens ?? 4096` 改为 `?? ModelLimitDefaults.Output`（正常路径下 `PrepareClientRequest` 已回填，兜底仅覆盖模型 limit 也为空的极端情况）。

`TextCompletionService.DefaultMaxTokens`（`TextCompletionService.cs:35`）是否同步调整——属文本补全子服务，默认保持 4096，避免影响标题生成等短输出场景。

### 7.4 其他硬编码 4096

- `PredefinedModels.cs:49`（gpt-4-turbo Output=4096）为具体模型真实值，不改。
- 测试中的 4096 断言随实现调整。

## 8. 测试

- `ModelConfigManager`：
  - 白名单为空 → `GetModels()` 为空；`GetCatalogModels()` 仍为全量。
  - 白名单含某 ID → 启用视图只含该模型；`GetModel(隐藏id)` 仍返回。
  - `SetEnabledModelsAsync` 写配置并触发 `ModelConfigChanged`；不重拉 Provider（Provider 调用计数不变）。
  - `ModelReloadHandler` 收到 `ModelSelection` 变更 → 仅重建视图。
- `ModelLimits` 默认值断言更新为 200000 / 65536。
- WebUI：ModelsPage 勾选 → 选择器可见集合变化（集成/手测）。
- 现有 `ModelCatalogAggregationTests` 中依赖 `GetModels()` 全量的断言需按新语义调整（改用 `GetCatalogModels()`）。

## 9. 影响面清单

| 文件 | 变更 |
|------|------|
| `src/abstractions/.../Llm/ModelSelectionConfig.cs` | 新增 DTO |
| `src/abstractions/.../Llm/IModelConfigManager.cs` | 新增 4 个方法、语义注释更新 |
| `src/abstractions/.../Llm/ModelConfig.cs` | `ModelLimits` 默认值 200k/64k |
| `src/abstractions/.../Llm/ModelCapabilities/ModelLimitDefaults.cs` | 新增回落常量 |
| `src/spine/.../Configuration/ConfigSectionRegistry.cs` | 注册 `ModelSelection` 节 |
| `src/spine/.../Llm/ModelConfigManager.cs` | 双缓存、白名单应用、新方法、`SetEnabledModelsAsync` |
| `src/spine/.../Llm/ModelReloadHandler.cs` | 处理 `ModelSelection` 变更 |
| `src/spine/.../Llm/LlmService.cs` | 兜底 `?? 4096` 改 `ModelLimitDefaults.Output` |
| `src/spine/.../Llm/ModelManager.cs` | 透传新方法、`NormalizeCatalogRef` 改用全量 |
| `src/spine/.../Llm/IModelManager.cs` | 透传新方法 |
| `samples/.../Pages/ModelsPage.razor` | 显示全部开关、启用列、两模式添加弹窗、limit 默认 |
| `samples/.../Components/SessionWindow.razor` | 发送前存在性判断改用全量 |
| 测试若干 | 见 §8 |

## 10. 已定事项（原待确认点）

1. **出站 `maxTokens` 默认**：请求未指定时回落模型 `Limit.Output`（`PrepareClientRequest` 已有机制），兜底由 4096 改为 `ModelLimitDefaults.Output`。
2. **`ModelSelection` 级别**：User 级（`~/.seeing/seeing.json`），与 `Providers` 一致。
