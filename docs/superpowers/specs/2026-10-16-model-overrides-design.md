# 模型元数据用户覆盖层（ModelOverrides）设计

**日期**: 2026-10-16
**状态**: 已确认（方案 A）

## 问题

1. `/models` 页模型列表无搜索/过滤/分页，远端模型多时难以查找。
2. 扩展 Provider（远端 `/v1/models`）模型被双重锁（UI `CanEditCurrentProvider` + Manager `RequireWritableProvider` 静默跳过）完全不可编辑。
3. 类型标签（TEXT/EMBEDDING/IMAGE）错误无法修正：远端映射不返回类型（SiliconFlow 全空、OpenCodeZen 硬编码 `[Text]`），而能力目录 FillEmpty 只填空、不处理 `ModelConfig.Types`。

## 方案（A）

新增**用户级覆盖节**，在 `ModelConfigManager` 聚合链末端应用；UI 在 `/models` 页做查找增强 + 编辑弹窗解锁。

### 数据模型

- 节名 `ModelOverrides`，**UserOnly**，独立文件 `~/.seeing/model-overrides.json`，注册进 `ConfigSectionRegistry`（与 `Providers`/providers.json 同惯例）。
- 键 = `providerId/modelId`，值 = 稀疏补丁（全字段可空，未设置键跟随远端默认）：

```json
{
  "siliconflow/Qwen/Qwen2.5-7B-Instruct": { "types": ["Embedding"] },
  "opencode-zen/gpt-4o": { "types": ["Text","Image"], "contextLimit": 128000 }
}
```

- 补丁字段：`types`（`List<ModelType>`）、`name`、`contextLimit`、`outputLimit`、`inputModalities`、`outputModalities`、`isFree`。
- **补录**：键在目录中不存在 → 视为新增模型，缺省 Types=`[Text]`，照常走 `ModelSelection` 白名单启用。

### 聚合链

`RefreshFromProvidersAsync`：远端/配置模型 → FillEmpty 能力丰富 → **ApplyOverrides（字段覆盖 + 补录插入）** → 白名单启用视图。

覆盖语义 = 显式键覆盖（非填空）：用户写的键永远赢；未写键随远端刷新更新。保存时 **diff 持久化**：只落盘与"无覆盖基线"不同的字段，避免固化默认值。

### 接口（IModelConfigManager 扩展）

- `ModelOverride? GetModelOverride(providerId, modelId)` — UI"已覆盖"标记。
- `Task UpsertModelOverrideAsync(providerId, modelId, patch, ct)` — 读改写节 + 原子落盘。
- `Task RemoveModelOverrideAsync(providerId, modelId, ct)` — 恢复默认。

### 热重载

`ModelReloadHandler` 响应 `ModelOverrides` 节变更 → **轻量重聚合**（不重拉远端：从缓存 provider 模型重走 FillEmpty + 覆盖 + 白名单）→ `ModelConfigChanged` → 会话选择器/Settings/TokenBudget/Memory 自动刷新。

### UI（ModelsPage）

- 搜索框（ID/名称，300ms debounce）+ 类型多选筛选 + 分页 20/页（抄 `ModelCapabilitiesSettingsCard` 模式）。
- 扩展 Provider 解锁"编辑/添加模型"：走覆盖层写回；远端模型"删除"按钮对扩展 Provider 隐藏（"恢复默认"即等价语义）。
- 编辑弹窗：名称/类型多选/上下文/最大输出/输入模态多选/免费标记；表单显示当前生效值，小字提示基线值；已覆盖行加 Tag；操作列"恢复默认"= 移除覆盖。
- 写回失败 Toast 报错。

### 辅助兜底（方案 C 残留）

远端模型名强约定匹配（`embedding|rerank` 等）时自动标类型，减少手工修正量。

## 边界

- 不引入项目级/两级合并（用户级已确认）。
- 不做"隐藏模型"（现有启用白名单已覆盖该语义）。
- 能力目录（ModelCapabilityEntry/FillEmpty）不动语义。

## 分层与归属（纪律）

- **Abstractions 零实现**：只放 DTO/接口（`ModelOverride`、`IModelConfigManager`）。启发式实现（`ModelTypeInference`）**不得**放 Abstractions。
- **共享底层包 `Seeing.Agent.Llm.Support`**（新，置于 `src/support/`）：仅依赖 Abstractions，禁止引用任何上层包；承载模型类型命名约定推断等启发式实现，供 provider 插件与 Core 复用。
- **`src/support/` 分组**：共享/底层支持包与具体能力包物理分离，避免 `capabilities/` 混杂。`Seeing.Agent.Tools.Support` 一并从 `capabilities/` 迁入。
- **覆盖应用逻辑抽为纯函数** `ModelOverrideApplier`（Core 内，static）：`ApplyAll` / `Apply` / `Diff` / `Clone`，与配置/IO 解耦，便于测试。
- **`GetModelBaseline` 为内部实现细节**，不进 `IModelConfigManager` 契约。
- **统一轻量重建入口** `RebuildViews()`（覆盖 `ModelSelection` 与 `ModelOverrides` 两类节变更）。

## 测试

- `tests/spine/Seeing.Agent.Tests`：覆盖字段应用/稀疏合并/补录/移除/轻量重建/diff 持久化。
- 沿用现有节注册与原子写回基建测试。
