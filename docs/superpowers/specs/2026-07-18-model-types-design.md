# 模型用途类型（ModelType）设计

**日期:** 2026-07-18  
**状态:** 待实现  
**范围:** `Seeing.Agent` 模型元数据 + WebUI 选模过滤  

---

## 1. 背景与目标

### 问题

- `ModelConfig` 仅有 `modalities`（input/output：text/image/audio），描述多模态 I/O，**不能**区分 Embedding、Rerank、生图等**用途**。
- 记忆 Embedding、抽取模型等配置依赖手填 Provider/Model，易错。
- 会话 / Agent 选模若混入 Embedding 等非对话模型，会导致误用。

### 目标

1. 在模型管理中为每个模型打**多标签用途类型**（固定枚举）。
2. 选模 API **默认只返回 Text**；显式指定类型后才按该类型过滤。
3. 会话页、Agent 配置、抽取模型、Embedding 配置均改为类型感知下拉，减少手填错误。

### 非目标（本版）

- 不从模型名启发式推断类型。
- 不新建 Image/Video/Speech 专用业务配置页（仅支持打标签 + 通用查询）。
- 不改变 `Memory.Embedding` / `Extraction` 的持久化字段结构（仍为 Provider + Model 字符串）。
- 不替代现有 `modalities` 字段。

---

## 2. 决策摘要

| 项 | 选择 |
|----|------|
| 表达方式 | 多标签（一个模型可属多种类型） |
| 取值 | 固定枚举，不可自定义字符串 |
| 枚举名 | `Text`（不用 Chat）、`Embedding`、`Rerank`、`Image`、`Video`、`Speech`、`Other` |
| 缺省 | `types` 缺失或空 → 运行时视为 `[Text]`，不强制写回文件 |
| 存储 | `ModelConfig.Types`，JSON 字段 `types` |
| 查询默认 | `GetModelsByType()` 默认 `type = Text` |
| 管理页 | 仍用 `GetModels()` / `GetModelsByProvider()` 看全量 |

---

## 3. 数据模型

```csharp
public enum ModelType
{
    Text,        // 文本对话 / 补全（默认）
    Embedding,
    Rerank,
    Image,
    Video,
    Speech,
    Other
}
```

`ModelConfig` 新增：

```csharp
/// <summary>用途类型（多标签）。空/缺失时有效类型为 [Text]。</summary>
[JsonPropertyName("types")]
public List<ModelType> Types { get; set; } = new();
```

与 `modalities` 关系：

| 字段 | 含义 |
|------|------|
| `types` | 用途分类（能否出现在 Embedding 下拉 / 对话选模） |
| `modalities` | 多模态 I/O（如 Text 模型是否支持看图） |

二者并存，互不推断。

### 配置示例

```json
{
  "Models": {
    "openai/gpt-4o-mini": {
      "id": "gpt-4o-mini",
      "provider": "openai",
      "types": ["Text"],
      "limit": { "context": 128000, "output": 16384 }
    },
    "openai/text-embedding-3-small": {
      "id": "text-embedding-3-small",
      "provider": "openai",
      "types": ["Embedding"]
    },
    "openai/gpt-4o": {
      "id": "gpt-4o",
      "provider": "openai",
      "types": ["Text", "Image"]
    }
  }
}
```

JSON 序列化：枚举使用字符串名（与项目现有 JsonStringEnumConverter 习惯对齐；若全局未开，本字段显式配置）。

---

## 4. 查询 API

扩展 `IModelConfigManager`：

```csharp
/// <summary>解析有效类型：Types 空/null → [Text]</summary>
IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config);

/// <summary>
/// 按类型过滤。默认 type = Text。
/// 多标签：EffectiveTypes 包含该类型即命中。
/// </summary>
IReadOnlyDictionary<string, ModelConfig> GetModelsByType(
    ModelType type = ModelType.Text,
    string? providerId = null);
```

### 行为表

| 调用 | 结果 |
|------|------|
| `GetModelsByType()` / `GetModelsByType(Text)` | 仅 Text（含未写 types 的旧模型） |
| `GetModelsByType(Embedding)` | 仅标记了 Embedding 的模型 |
| `GetModels()` / `GetModelsByProvider()` | 全量（模型管理用） |

命中规则：对每条 `ModelConfig`，取 `GetEffectiveTypes(config)`，若包含目标 `type` 则纳入结果。键仍为现有模型字典键（如 `openai/gpt-4o-mini`）。

---

## 5. UI 改造

### 5.1 选模数据源约定

| 场景 | 数据源 |
|------|--------|
| 会话页选模 | `GetModelsByType()`（默认 Text） |
| Agent 配置模型 | 同上 |
| 记忆「抽取模型」 | 同上 |
| 记忆 Embedding | `GetModelsByType(Embedding)` |
| 模型管理页 | `GetModels()` / `GetModelsByProvider()` |

本版**不提供**「显示全部类型」开关；全量仅管理页可见。

### 5.2 ModelsPage（`/models`）

- 表格新增「类型」列：展示有效类型（空显示为 Text）。
- 编辑：多选 `Select`，选项为全部 `ModelType`。
- 保存：`UpdateModelAsync` / `SaveModelsAsync` 写入 `types`。
- 「设为默认」：仅当有效类型含 `Text` 时允许；否则提示不可设为默认对话模型。

### 5.3 记忆设置（`/memory/settings`）

- **Embedding**：下拉来自 `GetModelsByType(Embedding)`，展示 `provider / id`（或 name）；选中后写入 `Memory.Embedding.Provider` + `Model`。列表为空时提示先到模型管理勾选 Embedding。
- **抽取模型**：下拉来自 `GetModelsByType(Text)`；写入 `Extraction.Model`；允许空（表示使用 DefaultModel）。
- 「测试连接」逻辑不变。

### 5.4 会话页 / Agent 配置

- 现有可用模型列表改为 `GetModelsByType(Text)`（或默认参数调用）。
- 若会话/Agent 已保存的模型后来被改为不含 Text：下拉保留当前值并显示警告，或在保存时引导改回 Text 模型（实现时二选一，优先「保留 + 警告」以免丢配置）。

---

## 6. 兼容与迁移

1. 旧配置无 `types`：运行时视为 `[Text]`，行为与改造前对话选模一致。
2. 用户从未保存类型：不强制改写 `seeing.json`。
3. 已手填的 Embedding Provider/Model：只要能在 `Models` 中匹配到对应条目且（用户补标后）含 Embedding，下拉可预选；否则保留手填值显示为「自定义/未登记」直至用户重选（实现细节可简化为：匹配不到则显示当前字符串 + 空列表提示）。

---

## 7. 测试要点

- `GetEffectiveTypes`：空 / null / `[Text]` / 多标签。
- `GetModelsByType()` 默认不含仅 Embedding 的模型。
- `GetModelsByType(Embedding)` 不含仅 Text 的模型。
- 多标签 `["Text","Image"]`：Text 查询与 Image 查询均命中。
- 设为默认：非 Text 模型被拒绝。
- 序列化往返：`types` 字符串数组。

---

## 8. 实现顺序建议

1. `ModelType` + `ModelConfig.Types` + `GetEffectiveTypes` / `GetModelsByType` + 单元测试。
2. ModelsPage 类型编辑与设默认校验。
3. 会话页 / Agent 配置选模改为 Text。
4. 记忆设置：Embedding + 抽取下拉。

---

## 9. 开放问题（已关闭）

| 问题 | 结论 |
|------|------|
| 单类型 vs 多标签 | 多标签 |
| Chat vs Text | 使用 `Text` |
| 缺省行为 | 视为 `[Text]` |
| 查询默认 | 默认只查 Text |
| UI 范围 | 管理页 + 会话 + Agent + 记忆 Embedding/抽取 |
