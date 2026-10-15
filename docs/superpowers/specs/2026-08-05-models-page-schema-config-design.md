# Models 页 Provider 连接配置：Schema 驱动双轨 UI

**日期:** 2026-08-05  
**状态:** 待实现  
**范围:** Provider 连接/选项区 redesign；模型列表 CRUD 不在本变更内大幅改动

## 背景

`ModelsPage` 当前对手写 `ProviderConfig` 表单与扩展 Provider 只读描述做了分支。Gateway 客户端已有 `ConfigFieldSchema` + `SchemaDrivenConfigForm` 的动态配置模式。在 LLM Provider 已抽象为 `ILlmProvider` 之后，需要：

1. 自定义/扩展 Provider 能按自身 schema 自动生成可配置页（类似 Gateway）
2. 内置（Configured）Provider **继续使用当前手写样式**
3. 对外可配置能力用统一可选接口；内置由 `ConfiguredLlmProvider` 实现，内部仍写 `seeing.json`
4. 扩展自管存储，不强制落入 `Providers[id]`

## 目标

- 抽出无 UI 依赖的 `Seeing.ConfigSchema` 库，Gateway 与 Agent 共用
- 引入 `IConfigurableLlmProvider`；WebUI 仅通过 `IProviderManager` 发现可配置能力（不注入 `IProviderRegistry`）
- Models 页连接区双轨渲染 + 详情区独立「保存配置」
- 破坏性迁移，不保留旧命名空间兼容/type-forwarding

## 非目标

- 模型列表 / 模型字段 schema 化
- 扩展 Provider 的「添加」向导
- 将 Blazor 表单组件抽到独立 UI 包
- 删除 `IProviderManager.SaveProviderAsync`（可继续被内置 Save 内部调用）
- 内置表单改为 schema 生成

## 架构总览

```
Seeing.ConfigSchema          (ConfigFieldSchema / ConfigFieldType / OptionsSchemaBuilder)
        ↑
Seeing.Gateway*  /  Seeing.Agent (IConfigurableLlmProvider)  /  WebUI (SchemaDrivenConfigForm)

ModelsPage 连接区:
  Configured  → 手写表单 → SaveConfigAsync (seeing.json)
  Extension + schema → SchemaDrivenConfigForm → SaveConfigAsync (扩展自管)
  Extension 无接口 → 只读
```

## 1. 共享库 `Seeing.ConfigSchema`

### 项目

- 路径: `src/Seeing.ConfigSchema/`
- TFM: `net10.0`
- 依赖: 无（仅 BCL + 现有反射所需的 DataAnnotations 等）
- 加入 `Seeing.Agent.slnx`

### 迁入（自 `Seeing.Gateway.Configuration`）

| 类型 | 新命名空间 |
|------|------------|
| `ConfigFieldSchema` | `Seeing.ConfigSchema` |
| `ConfigFieldType` | `Seeing.ConfigSchema` |
| `OptionsSchemaBuilder` | `Seeing.ConfigSchema` |

### 迁移规则

- 删除 Gateway 中旧文件
- 所有引用改为 `using Seeing.ConfigSchema`
- **不做** type-forwarding / Obsolete 别名

### 引用方

- `Seeing.Gateway`、`Seeing.Gateway.QQ`、`Seeing.Gateway.WeCom`
- `Seeing.Agent`（接口返回类型）
- `Seeing.Agent.WebUI`
- 相关测试项目按需引用

### 测试

- 新建 `tests/Seeing.ConfigSchema.Tests`，覆盖 `OptionsSchemaBuilder.FromType` 的类型推断、Required、Secret/Url 启发式、Browsable 跳过等核心行为

## 2. `IConfigurableLlmProvider`

位置: `src/Seeing.Agent/Llm/IConfigurableLlmProvider.cs`

```csharp
public interface IConfigurableLlmProvider
{
    /// <summary>
    /// null 或空列表：宿主应使用手写 UI（内置）。
    /// 非空：宿主使用 schema 驱动表单。
    /// </summary>
    IReadOnlyList<ConfigFieldSchema>? GetConfigSchema();

    Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(
        CancellationToken cancellationToken = default);

    Task SaveConfigAsync(
        IReadOnlyDictionary<string, object?> values,
        ConfigLevel level,
        CancellationToken cancellationToken = default);
}
```

### 实现约定

| 实现者 | `GetConfigSchema` | 持久化 |
|--------|-------------------|--------|
| `ConfiguredLlmProvider` | 恒为 `null` | Load：由当前 `ProviderConfig` 映射为扁平字典；Save：字典写回连接字段并持久化到 `Providers[id]`，尊重 `ConfigLevel`。可内部调用现有 `SaveProviderAsync` / `UnifiedConfigManager` 路径。字典**不含** `Models` |
| 扩展 Provider（可选） | 返回真实字段 | 自管存储；可忽略 `level` |

### 内置字典键（与手写表单对齐）

至少包括: `Name`、`Type`、`BaseUrl`、`ApiKey`；按现有表单/配置需要可含 `Timeout`、`MaxRetries`、`DefaultModel` 等连接相关字段。不包含模型目录。

### `IProviderManager` 发现 API

```csharp
bool TryGetConfigurable(string providerId, out IConfigurableLlmProvider? configurable);
```

- 未知 id / 未实现接口 → `false`，`configurable = null`
- WebUI **不**注入 `IProviderRegistry`

### `ConfiguredLlmProvider` 与 UI 绑定

- 手写表单可继续绑定页面侧的 `ProviderConfig` 编辑副本（来自配置快照），不必强制用 `LoadConfigAsync` 驱动控件
- 「保存配置」时将表单字段映射为字典后调用 `SaveConfigAsync`
- `LoadConfigAsync` 仍需实现，保证接口完整，并供测试/其它宿主使用
- Schema 路径（扩展）：进入详情时调用 `LoadConfigAsync` 填充 `Dictionary<string, object?>`，交给 `SchemaDrivenConfigForm`

## 3. Models 页交互

### 布局

保持左列表 + 右连接区 + 右下模型表。模型 CRUD / 测试连接 / 设默认模型逻辑保持现有行为（仍可用页顶 `ConfigLevel`）。

### 连接区渲染优先级

1. `TryGetConfigurable` 成功且 `GetConfigSchema()` 返回非空列表 → `SchemaDrivenConfigForm` + 详情区「保存配置」
2. `Source == Configured` → 现有手写表单 + 详情区「保存配置」
3. 否则 → 只读 Descriptions +「扩展 Provider 由扩展管理」类提示

> 注：内置因 `GetConfigSchema() == null`，不会误入 schema 分支。扩展实现了接口但 schema 为 null/空时，按第 3 条只读处理（本设计不提供扩展手写表单宿主）。

### 保存分流

| 入口 | 职责 |
|------|------|
| 详情区「保存配置」 | 仅 Provider 连接选项 → `SaveConfigAsync(values, _saveLevel)` |
| 页顶 | **移除**页顶「保存」按钮（其原职责仅为 `SaveProviderAsync`）。**保留** `ConfigLevel` 下拉，供详情「保存配置」与模型相关 API 共用 |

### 其它

- 「添加 Provider」仍只创建内置 Configured（OpenAI/Anthropic）
- 切换 Provider 时丢弃未保存脏数据（不弹确认，与现状一致）
- 测试连接：两轨皆保留，继续 `IProviderManager.TestConnectionAsync`

## 4. Gateway / WebUI 组件

### Gateway

- 插件与 `GatewayChannelTypeInfo` 等改用 `Seeing.ConfigSchema`
- 行为不变

### WebUI

- `SchemaDrivenConfigForm` / `SchemaFieldEditor` 改引用共享库
- 建议从 `Components/GatewayClients/` 挪到 `Components/ConfigSchema/`（Gateway 抽屉与 Models 页共用）
- `GatewayClientDrawer` 行为回归保持不变

## 5. 错误处理

- `SaveConfigAsync` 失败：详情区提示，不吞异常
- `LoadConfigAsync` 失败：Alert，不渲染半残表单
- Schema 路径：先 `TryValidate`，失败不 Save
- 内置手写：不新增比现状更严的校验；连接问题仍可由「测试连接」暴露
- 扩展忽略 `ConfigLevel` 为预期行为

## 6. 验收标准

1. Gateway 客户端配置抽屉与迁移前行为一致
2. 内置 Provider：手写样式；详情「保存配置」写入对应 level 的 `Providers[id]`
3. 实现接口且有非空 schema 的扩展：自动表单；Load/Save 走扩展实现
4. 未实现接口的扩展：只读
5. 模型 CRUD / 测试连接 / 设默认模型可用
6. WebUI 不注入 `IProviderRegistry`
7. 无旧命名空间兼容层；`dotnet build` 通过；`Seeing.ConfigSchema` 与 Agent 相关单测通过

## 7. 实现顺序建议

1. 创建 `Seeing.ConfigSchema` 并迁移 Gateway 引用（可独立验证 Gateway UI）
2. 新增 `IConfigurableLlmProvider` + `ConfiguredLlmProvider` 实现 + `TryGetConfigurable`
3. 调整 `ModelsPage` 双轨 UI 与保存分流
4. 移动/复用 `SchemaDrivenConfigForm`；扩展路径接好
5. 单测 + 手工回归（Models + Gateway 抽屉）

## 决策记录

| 决策 | 选择 |
|------|------|
| 范围 | 仅 Provider 连接区（A） |
| 扩展持久化 | 扩展自管（B） |
| 可配置契约 | 可选接口 `IConfigurableLlmProvider`（B） |
| Schema 类型 | 独立库 `Seeing.ConfigSchema`（A） |
| 库内容 | 纯模型 + OptionsSchemaBuilder，无 Blazor（A） |
| 保存 UX | 详情区独立「保存配置」（B） |
| 内置实现者 | `ConfiguredLlmProvider` 自身实现（A） |
| 整体方案 | 双轨渲染 + 统一可配置接口（方案 1） |
| 兼容层 | 无 |
| Git | 本变更设计/实现过程按用户要求不主动 commit |
