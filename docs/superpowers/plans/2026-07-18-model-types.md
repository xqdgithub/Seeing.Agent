# Model Types Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 `ModelConfig` 增加多标签用途类型 `ModelType`，选模默认只查 `Text`，并改造 WebUI（模型管理、会话、Agent、记忆 Embedding/抽取）按类型过滤。

**Architecture:** 在 `ModelConfig.Types` 存固定枚举列表；`IModelConfigManager.GetEffectiveTypes` / `GetModelsByType` 负责缺省 `[Text]` 与过滤；`ILlmService.GetAvailableModels` 改为默认返回 Text 模型，使会话/Agent 等调用点一并收紧；模型管理页继续用全量 `GetModels`/`GetModelsByProvider`。

**Tech Stack:** .NET 10 / C#、xUnit + FluentAssertions、Blazor Server + AntDesign、`seeing.json` Models 节

**Spec:** `docs/superpowers/specs/2026-07-18-model-types-design.md`

## Global Constraints

- 枚举名使用 `Text`（不是 Chat）；缺省空 `types` → 运行时 `[Text]`
- `GetModelsByType` 默认参数 `type = ModelType.Text`
- 不推断类型；不改 `Memory.Embedding`/`Extraction` 字段形状
- 与 `modalities` 并存、互不替代
- **Git：** 仅在用户明确要求时 commit；实现时默认跳过各 Task 的 Commit 步骤

## File map

| 文件 | 职责 |
|------|------|
| `src/Seeing.Agent/Llm/ModelType.cs` | 枚举定义 |
| `src/Seeing.Agent/Llm/ModelConfig.cs` | 新增 `Types` 属性 |
| `src/Seeing.Agent/Llm/IModelConfigManager.cs` | 新查询 API |
| `src/Seeing.Agent/Llm/ModelConfigManager.cs` | 实现过滤与 EffectiveTypes |
| `src/Seeing.Agent/Llm/LlmService.cs` | `GetAvailableModels` → Text |
| `tests/Seeing.Agent.Tests/Llm/ModelTypeFilterTests.cs` | 单元测试 |
| `samples/.../Pages/ModelsPage.razor` | 类型编辑 + 设默认校验 |
| `samples/.../Shared/MainLayout.razor` | AppState 模型列表改 Text |
| `samples/.../Pages/MemorySettingsPage.razor` | Embedding/抽取下拉 |
| `samples/.../Pages/Session.razor` / `AgentConfigPage.razor` | 依赖 `GetAvailableModels` 即可（验证） |

---

### Task 1: ModelType + Types + GetModelsByType（TDD）

**Files:**
- Create: `src/Seeing.Agent/Llm/ModelType.cs`
- Modify: `src/Seeing.Agent/Llm/ModelConfig.cs`
- Modify: `src/Seeing.Agent/Llm/IModelConfigManager.cs`
- Modify: `src/Seeing.Agent/Llm/ModelConfigManager.cs`
- Create: `tests/Seeing.Agent.Tests/Llm/ModelTypeFilterTests.cs`

**Interfaces:**
- Produces: `ModelType` enum；`ModelConfig.Types`；`GetEffectiveTypes(ModelConfig)`；`GetModelsByType(ModelType type = ModelType.Text, string? providerId = null)`

- [ ] **Step 1: 写失败测试**

创建 `tests/Seeing.Agent.Tests/Llm/ModelTypeFilterTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ModelTypeFilterTests
{
    [Fact]
    public void GetEffectiveTypes_WhenEmpty_ShouldBeTextOnly()
    {
        var mgr = CreateManager(new Dictionary<string, ModelConfig>());
        var types = mgr.GetEffectiveTypes(new ModelConfig { Id = "m", Provider = "p" });
        types.Should().Equal(ModelType.Text);
    }

    [Fact]
    public void GetModelsByType_Default_ShouldExcludeEmbeddingOnly()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["openai/gpt"] = new() { Id = "gpt", Provider = "openai", Types = [ModelType.Text] },
            ["openai/emb"] = new() { Id = "emb", Provider = "openai", Types = [ModelType.Embedding] },
            ["openai/legacy"] = new() { Id = "legacy", Provider = "openai" } // 空 types
        };
        var mgr = CreateManager(models);

        var text = mgr.GetModelsByType();
        text.Keys.Should().BeEquivalentTo("openai/gpt", "openai/legacy");

        var emb = mgr.GetModelsByType(ModelType.Embedding);
        emb.Keys.Should().BeEquivalentTo("openai/emb");
    }

    [Fact]
    public void GetModelsByType_MultiLabel_ShouldHitBoth()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["openai/multi"] = new()
            {
                Id = "multi",
                Provider = "openai",
                Types = [ModelType.Text, ModelType.Image]
            }
        };
        var mgr = CreateManager(models);
        mgr.GetModelsByType(ModelType.Text).Should().ContainKey("openai/multi");
        mgr.GetModelsByType(ModelType.Image).Should().ContainKey("openai/multi");
    }

    [Fact]
    public void GetModelsByType_WithProvider_ShouldFilter()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["openai/a"] = new() { Id = "a", Provider = "openai", Types = [ModelType.Text] },
            ["anthropic/b"] = new() { Id = "b", Provider = "anthropic", Types = [ModelType.Text] }
        };
        var mgr = CreateManager(models);
        mgr.GetModelsByType(ModelType.Text, "openai").Keys.Should().BeEquivalentTo("openai/a");
    }

    private static ModelConfigManager CreateManager(Dictionary<string, ModelConfig> models)
    {
        // 使用可注入的轻量 stub：若现有测试无 UnifiedConfigManager 工厂，
        // 则把 GetEffectiveTypes / 过滤逻辑抽成 internal static ModelTypeHelper 并单测 Helper；
        // 优先直接测 Manager —— 构造 SeeingAgentOptions.Models 的 UnifiedConfigManager 测试替身见下方实现任务。
        throw new NotImplementedException("replace with real fixture in Step 3");
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~ModelTypeFilterTests" --verbosity minimal
```

Expected: 编译失败或 `GetEffectiveTypes` / `GetModelsByType` 不存在。

- [ ] **Step 3: 实现类型与 API**

创建 `src/Seeing.Agent/Llm/ModelType.cs`：

```csharp
namespace Seeing.Agent.Llm;

public enum ModelType
{
    Text,
    Embedding,
    Rerank,
    Image,
    Video,
    Speech,
    Other
}
```

在 `ModelConfig.cs` 的 `ModelConfig` 类中增加：

```csharp
/// <summary>用途类型（多标签）。空/缺失时有效类型为 [Text]。</summary>
[JsonPropertyName("types")]
public List<ModelType> Types { get; set; } = new();
```

在 `IModelConfigManager` 查询区增加：

```csharp
IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config);

IReadOnlyDictionary<string, ModelConfig> GetModelsByType(
    ModelType type = ModelType.Text,
    string? providerId = null);
```

在 `ModelConfigManager` 实现：

```csharp
public IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config)
{
    if (config.Types is { Count: > 0 })
        return config.Types;
    return new[] { ModelType.Text };
}

public IReadOnlyDictionary<string, ModelConfig> GetModelsByType(
    ModelType type = ModelType.Text,
    string? providerId = null)
{
    IEnumerable<KeyValuePair<string, ModelConfig>> query = GetModels();
    if (!string.IsNullOrEmpty(providerId))
        query = query.Where(kv =>
            string.Equals(kv.Value.Provider, providerId, StringComparison.OrdinalIgnoreCase)
            || kv.Key.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase));

    return query
        .Where(kv => GetEffectiveTypes(kv.Value).Contains(type))
        .ToDictionary(kv => kv.Key, kv => kv.Value);
}
```

**测试夹具：** 若 `UnifiedConfigManager` 难以直接构造，将 `GetEffectiveTypes`/`FilterByType` 抽到 `internal static class ModelTypeRules`，Manager 委托调用，测试直接测 `ModelTypeRules`；对外 API 仍挂在 Manager。

示例抽离：

```csharp
internal static class ModelTypeRules
{
    public static IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config)
        => config.Types is { Count: > 0 } ? config.Types : new[] { ModelType.Text };

    public static bool Matches(ModelConfig config, ModelType type)
        => GetEffectiveTypes(config).Contains(type);
}
```

此时测试可不依赖 UnifiedConfigManager，直接断言 `ModelTypeRules`；另加一个对 Manager 的集成测试若已有 config fixture 再补。

- [ ] **Step 4: 跑通测试**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~ModelTypeFilter" --verbosity minimal
```

Expected: PASS

- [ ] **Step 5: Commit（可选，仅用户要求时）**

```bash
git add src/Seeing.Agent/Llm/ModelType.cs src/Seeing.Agent/Llm/ModelConfig.cs src/Seeing.Agent/Llm/IModelConfigManager.cs src/Seeing.Agent/Llm/ModelConfigManager.cs tests/Seeing.Agent.Tests/Llm/ModelTypeFilterTests.cs
git commit -m "feat(llm): add ModelType multi-label and GetModelsByType filter"
```

---

### Task 2: GetAvailableModels 默认 Text + SetDefaultModel 校验

**Files:**
- Modify: `src/Seeing.Agent/Llm/LlmService.cs`
- Modify: `src/Seeing.Agent/Llm/ILlmService.cs`（若接口注释需更新）
- Modify: `src/Seeing.Agent/Llm/ModelConfigManager.cs`（`SetDefaultModelAsync`）
- Modify: `tests/Seeing.Agent.Tests/Llm/ModelTypeFilterTests.cs`（或新建）

**Interfaces:**
- Consumes: `GetModelsByType`
- Produces: `GetAvailableModels()` 仅 Text；`SetDefaultModelAsync` 拒绝非 Text

- [ ] **Step 1: 写失败测试（默认模型校验）**

```csharp
[Fact]
public async Task SetDefaultModelAsync_WhenEmbeddingOnly_ShouldThrow()
{
    // Arrange: Manager 含 openai/emb Types=[Embedding]
    // Act/Assert: await Assert.ThrowsAsync<InvalidOperationException>(() =>
    //     mgr.SetDefaultModelAsync("openai/emb"));
}
```

若异步持久化难测，改为同步校验方法：

```csharp
public bool CanSetAsDefaultModel(string modelId)
{
    var config = GetModel(modelId);
    if (config is null) return false;
    return GetEffectiveTypes(config).Contains(ModelType.Text);
}
```

测试 `CanSetAsDefaultModel`；`SetDefaultModelAsync` 开头调用并在 false 时抛出。

- [ ] **Step 2: 运行确认失败**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CanSetAsDefaultModel|SetDefaultModelAsync_WhenEmbedding" --verbosity minimal
```

- [ ] **Step 3: 实现**

`LlmService.GetAvailableModels`：

```csharp
public IReadOnlyDictionary<string, ModelConfig> GetAvailableModels()
    => _modelManager.GetModelsByType(ModelType.Text);
```

更新接口注释：`获取可用于文本对话的模型（默认 Text）`。

`ModelConfigManager`：

```csharp
public bool CanSetAsDefaultModel(string modelId)
{
    var config = GetModel(modelId);
    return config is not null && GetEffectiveTypes(config).Contains(ModelType.Text);
}

public async Task SetDefaultModelAsync(string? modelId, ConfigLevel level = ConfigLevel.Project, CancellationToken ct = default)
{
    if (!string.IsNullOrEmpty(modelId) && !CanSetAsDefaultModel(modelId))
        throw new InvalidOperationException($"模型 '{modelId}' 不是 Text 类型，不能设为默认对话模型。");
    // ... existing save DefaultModel
}
```

在 `IModelConfigManager` 增加 `bool CanSetAsDefaultModel(string modelId);`

- [ ] **Step 4: 测试通过 + 构建**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelType" --verbosity minimal
dotnet build src/Seeing.Agent/Seeing.Agent.csproj --verbosity quiet
```

- [ ] **Step 5: Commit（可选）**

```bash
git commit -m "feat(llm): GetAvailableModels defaults to Text; guard default model"
```

---

### Task 3: ModelsPage 类型编辑

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/ModelsPage.razor`

**Interfaces:**
- Consumes: `ModelType`、`GetEffectiveTypes`、`CanSetAsDefaultModel`、`UpdateModelAsync`

- [ ] **Step 1: 扩展 ModelRow 与表格列**

在 `ModelRow` 增加：

```csharp
public List<ModelType> Types { get; set; } = new();
public string TypesDisplay { get; set; } = "Text";
```

`RefreshModelRows`：

```csharp
var effective = ModelManager.GetEffectiveTypes(m.Value);
Types = effective.ToList(),
TypesDisplay = string.Join(", ", effective)
```

表格增加列：展示 `TypesDisplay`；操作列增加「编辑类型」按钮打开 Modal。

- [ ] **Step 2: 编辑 Modal（多选）**

```razor
<Select Mode="multiple"
        TItemValue="ModelType"
        TItem="ModelType"
        @bind-Values="_editTypes"
        Style="width: 100%;">
    @foreach (var t in Enum.GetValues<ModelType>())
    {
        <SelectOption TItemValue="ModelType" TItem="ModelType" Value="@t" Label="@t.ToString()" />
    }
</Select>
```

保存：

```csharp
var config = ModelManager.GetModel(_editKey);
if (config is null) return;
config.Types = _editTypes?.ToList() ?? new List<ModelType>();
await ModelManager.UpdateModelAsync(_editKey, config, _saveLevel);
```

注意 AntDesign `Values` 绑定类型可能是 `IEnumerable<ModelType>`，按项目现有 Select 多选写法调整。

- [ ] **Step 3: 设为默认校验**

```csharp
private async Task SetDefaultModel(string key)
{
    if (!ModelManager.CanSetAsDefaultModel(key))
    {
        MessageService.Warning("仅 Text 类型模型可设为默认对话模型");
        return;
    }
    await ModelManager.SetDefaultModelAsync(key, _saveLevel);
    MessageService.Info($"已选择 {key} 为默认模型，请保存");
}
```

- [ ] **Step 4: 构建 WebUI**

```bash
dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj --verbosity minimal
```

Expected: 0 error

- [ ] **Step 5: Commit（可选）**

```bash
git commit -m "feat(webui): edit model types on ModelsPage"
```

---

### Task 4: 会话 / Agent / MainLayout 使用 Text 列表

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor`（`ModelManager.GetModels()` → `GetModelsByType()`）
- Verify: `Session.razor`、`AgentConfigPage.razor`、`Index.razor`（已用 `LlmService.GetAvailableModels()`，Task 2 后自动 Text）
- Optional warn: `Components/ModelSelector.razor` — 若当前值不在列表中，保留显示并提示

**Interfaces:**
- Consumes: `GetModelsByType` / `GetAvailableModels`

- [ ] **Step 1: 改 MainLayout**

将：

```csharp
AppState.AvailableModels = ModelManager.GetModels();
```

改为：

```csharp
AppState.AvailableModels = ModelManager.GetModelsByType();
```

- [ ] **Step 2: ModelSelector 保留孤儿值**

在 `ResolveDisplayValue` / 选项构建处：若 `Value` 非空且不在 `AvailableModels`，仍加入临时选项并在 UI 旁显示小字「非 Text 或已变更类型」：

```csharp
if (!string.IsNullOrEmpty(Value) && !AppState.AvailableModels.ContainsKey(Value))
{
    // 加入 orphan option，Label = Value + " (警告)"
}
```

- [ ] **Step 3: 构建并人工核对调用点**

```bash
rg "GetModels\(\)|GetAvailableModels" samples/Seeing.Agent.WebUI -g "*.razor" -g "*.cs"
dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj --verbosity minimal
```

Expected: 选模路径无裸 `GetModels()`（ModelsPage 除外）。

- [ ] **Step 4: Commit（可选）**

```bash
git commit -m "fix(webui): restrict chat model pickers to Text type"
```

---

### Task 5: 记忆设置 Embedding + 抽取下拉

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/MemorySettingsPage.razor`

**Interfaces:**
- Consumes: `IModelConfigManager.GetModelsByType(Embedding|Text)`；仍写 `MemoryOptions.Embedding` / `Extraction.Model`

- [ ] **Step 1: 注入 ModelManager**

```razor
@inject IModelConfigManager ModelManager
@using Seeing.Agent.Llm
```

- [ ] **Step 2: Embedding 下拉**

替换 Provider/Model 两个 `Input` 为单个（或双级）Select：

```csharp
private IEnumerable<EmbeddingModelOption> EmbeddingOptions =>
    ModelManager.GetModelsByType(ModelType.Embedding)
        .Select(kv => new EmbeddingModelOption(kv.Key, kv.Value.Provider, kv.Value.Id, kv.Value.Name));

private string? _embeddingKey; // "provider/id" 或字典键

private void OnEmbeddingSelected(string key)
{
    var cfg = ModelManager.GetModel(key);
    if (cfg is null) return;
    _options!.Embedding.Provider = cfg.Provider;
    _options.Embedding.Model = cfg.Id;
    _embeddingKey = key;
}
```

UI：

```razor
<Select TItemValue="string" TItem="EmbeddingModelOption"
        @bind-Value="_embeddingKey"
        OnSelectedItemChanged="..."
        Placeholder="选择 Embedding 模型"
        Style="width: 100%;">
    <SelectOptions>
        @foreach (var o in EmbeddingOptions)
        {
            <SelectOption TItemValue="string" TItem="EmbeddingModelOption"
                          Value="@o.Key" Label="@($"{o.Provider} / {(o.Name ?? o.Id)}")" />
        }
    </SelectOptions>
</Select>
@if (!EmbeddingOptions.Any())
{
    <Alert Type="Warning" Message="请先在模型管理中为模型勾选 Embedding 类型" ShowIcon="true" />
}
```

`OnInitialized`：根据已有 Provider+Model 反查键预选。

- [ ] **Step 3: 抽取模型下拉**

```razor
<Select AllowClear
        TItemValue="string"
        @bind-Value="_options.Extraction.Model"
        Placeholder="空则使用 DefaultModel">
    @foreach (var kv in ModelManager.GetModelsByType(ModelType.Text))
    {
        <SelectOption Value="@kv.Value.Id" Label="@($"{kv.Value.Provider} / {kv.Value.Name ?? kv.Value.Id}")" />
    }
</Select>
```

注意：`Extraction.Model` 历史可能存裸 id 或带 provider 前缀；选项 Value 与现有存储约定对齐（优先存 `Id`，与当前 Placeholder 行为一致）。若需 `provider/id`，全页统一一种。

- [ ] **Step 4: 构建 WebUI**

```bash
dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj --verbosity minimal
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ModelType" --verbosity minimal
```

Expected: 全绿

- [ ] **Step 5: Commit（可选）**

```bash
git commit -m "feat(webui): type-filtered model pickers on memory settings"
```

---

### Task 6: 端到端核对与 Spec 勾选

**Files:** 无新文件；对照 spec

- [ ] **Step 1: Spec coverage 检查表**

| Spec 要求 | Task |
|-----------|------|
| ModelType 枚举含 Text…Other | 1 |
| Types 多标签 + 缺省 Text | 1 |
| GetModelsByType 默认 Text | 1 |
| GetAvailableModels → Text | 2 |
| 设默认仅 Text | 2+3 |
| ModelsPage 编辑类型 | 3 |
| 会话/Agent Text | 2+4 |
| 记忆 Embedding/抽取下拉 | 5 |

- [ ] **Step 2: 手动冒烟清单**

1. 模型管理给 embedding 模型打 `Embedding`，对话模型保持空或 `Text`
2. 会话下拉只有 Text
3. 记忆 Embedding 下拉能选到该模型并保存/测试连接
4. 抽取下拉只有 Text；空=DefaultModel
5. 试图把 Embedding 设为默认 → 警告/失败

- [ ] **Step 3: 全量相关测试**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~ModelType|FullyQualifiedName~TextCompletion" --verbosity minimal
dotnet test tests/Seeing.Agent.Memory.Tests/Seeing.Agent.Memory.Tests.csproj --verbosity minimal
dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj --verbosity minimal
```

---

## Self-review (plan vs spec)

1. **Spec coverage:** 数据模型、API、ModelsPage、会话/Agent、记忆 Embedding+抽取、缺省 Text、默认查询 Text — 均有对应 Task。  
2. **Placeholders:** 测试夹具若 UnifiedConfigManager 难构造，已规定抽 `ModelTypeRules` 的明确退路，无 TBD。  
3. **Types:** `ModelType.Text` 贯穿；无 Chat 命名。
