# Provider 能力展示 Release Notes

**日期：** 2026-11-06
**范围：** WebUI 模型页（`samples/Seeing.Agent.WebUI`）+ `Seeing.ConfigSchema` + `Seeing.Agent.Llm.Support`

---

## 1. 症状

打开任意**扩展型** Provider（SiliconFlow / DeepSeek / MiMo 等）的模型页，`高级选项（1）` 里只有一个空白的能力输入框，
看不出这个 Provider 到底供出了什么能力。用户据此认为「这些 provider 没有自己标记能力」。

实际数据：`~/.seeing/providers/siliconflow.json` 内容只有 `{"apiKey": "..."}`，没有 `capabilities` 字段。

## 2. 根因

### 2.1 能力本来就有，只是没被呈现

能力由插件作者在代码里声明（`ConfigurableProviderBase.DefaultCapabilities`）：

| Provider | 声明位置 | 默认能力 |
|---|---|---|
| SiliconFlow | `SiliconFlowProvider.cs:60` | `chat` + `embedding` |
| DeepSeek | `DeepSeekProvider.cs:51` | `chat` |
| MiMo | `MiMoProvider.cs:51` | `chat` |
| TypeSafeApi | `TypeSafeApiProvider.cs:64` | `decision` |
| OpenCodeZen | `OpenCodeZenProvider.cs:59` | `chat` |

配置里 `capabilities` 为 `null` 时按 `ResolveCapabilitiesLocked` 回落到作者默认值
（`ConfigurableProviderBase.cs:140-143`）。**所以空框 ≠ 没有能力，空框 = 没有写覆盖。**

### 2.2 三处呈现缺陷

1. **schema 表单路径完全没有能力提示。** `ModelsPage.razor` 里 `ProviderCapabilitiesHint`
   只在手写表单分支渲染；扩展 Provider 走的是 `SchemaDrivenConfigForm`，那段提示根本不在渲染树上。

2. **提示文案把协议缺省当成 Provider 默认值。** 旧 `ProviderCapabilitiesHint` 用
   `LlmCapabilityMap.DefaultCapabilities(_currentProvider.Type)` 取默认值 —— 那是
   `ProviderCapabilityDefaults`（openai → 仅 chat）。对 SiliconFlow 这句话是**错的**：
   它的默认值是 chat + embedding。两个"默认"不是同一个概念：
   - 协议缺省 = 未配置 `capabilities` 且 **Provider 也没声明**时的兜底；
   - 插件默认 = Provider 作者写死的 `DefaultCapabilities`。
   对话时先说 `ProviderInfo.Capabilities`（运行时真相），不要自己再推一遍。

3. **提示读错了字段。** `ProviderCapabilitiesHint` 提前 `return` 于 `_currentProvider is null`，
   而 schema 路径下 `_currentProvider` 一直是 `null`（`SelectProviderAsync` 只在手写分支赋值），
   所以即使把提示挪进 schema 分支也是空串。

## 3. 修法

### 3.1 新增生效能力常显块（`ModelsPage.razor`）

Provider 卡片顶部（来源信息块之下）新增一块绿底提示：

```
生效能力：对话（chat）、向量（embedding）   [来源：插件默认]
```

数据来自 `ProviderInfo.Capabilities`（运行时投影），不经过任何推导。
来源标签区分三态（见 `ProviderCapabilityView.SourceText`）：

| 来源 | 文案 |
|---|---|
| 配置覆盖 | `来源：配置覆盖` |
| 配置显式空清单 | `来源：配置已关闭全部能力` |
| 未配置 + 扩展 Provider | `来源：插件默认` |
| 未配置 + 配置型 Provider | `来源：协议缺省（openai）` |

### 3.2 新增纯函数投影 `ProviderCapabilityProjection`

`samples/Seeing.Agent.WebUI/Components/Models/ProviderCapabilityProjection.cs`

把「生效位集」与「配置声明」分开建模，并提供漂移判定：

- `Build(info, declaredRaw)` — `declaredRaw` 接受 `List<string>`、`string[]`、
  `JsonElement`（配置从 JSON 读回时的真实形状）、逗号分隔文本；
- `IsDeclared` — 区分「键不存在 / null」（未声明）与「空数组」（显式关闭），两者语义不同；
- `UnknownKeys` — 未登记的能力键**原样保留**，不被吞掉（Provider 侧记警告并忽略）；
- `HasDrift` — 配置声明 ≠ 运行时生效，或存在未登记键；
- `DriftText` — 页面以 `Alert` 呈现漂移原因。

只告警不拦截：Provider 可能主动裁掉未实现的组合（如端点不提供 `/v1/embeddings`）。

### 3.3 `ConfigFieldSchema` 新增 `Placeholder`

`src/primitives/Seeing.ConfigSchema/ConfigFieldSchema.cs`

「留空有特殊含义」的字段（能力清单正是如此）只有占位文案能解释自己，否则空下拉看起来就是"这里什么都没有"。

`ConfigurableProviderBase.WithCapabilityChoices` 现在多收一个 `defaults` 参数，
生成 `留空 = 使用默认能力（chat, embedding）` 这类文案；`SchemaFieldEditor` 的
`StringList` 两个分支都改读 `Field.Placeholder`。

### 3.4 修 `ProviderCapabilitiesHint`

改为基于 `_capabilityView` 说话，不再自己推默认值：

- `Default` → `未配置覆盖 = 使用默认能力：对话（chat）、向量（embedding）。`
- `Declared` → `已声明 ...；全部取消后保存 = 交回该 Provider 的默认能力。`
- `Disabled` → `配置已显式关闭全部能力（该 Provider 当前不供出任何能力）；勾选任意项即可恢复。`

## 4. 用户可见效果（以截图场景为例）

打开 SiliconFlow（`providers/siliconflow.json` 只有 apiKey）：

- 修前：高级选项里一个空白框，无任何说明。
- 修后：顶部显示 `生效能力：对话（chat）、向量（embedding）` + `来源：插件默认`；
  能力多选占位文案为 `留空 = 使用默认能力（chat, embedding）`。

## 5. 测试

| 项目 | 用例 | 覆盖 |
|---|---|---|
| `Seeing.Agent.WebUI.Tests` | `ProviderCapabilityProjectionTests`（10） | 三态来源、显式空数组 vs 未声明、未登记键可见、漂移判定、JsonElement 读入、大小写归一 |
| `Seeing.Provider.DeepSeek.Tests` | `GetConfigSchema_Capabilities_ShouldExplainEmptyMeansDefault` | 占位文案含默认能力 |

回归：WebUI.Tests 222 通过、SiliconFlow.Tests 6 通过、DeepSeek.Tests 25 通过、
ConfigSchema.Tests 2 通过、`Seeing.Agent.Tests` 中 Llm/Provider 相关 512 通过，
全解决方案 `dotnet build Seeing.Agent.slnx` 0 错误。

## 6. 已知边界

- 能力多选框仍然只写 `ProviderConfig.Capabilities`（配置覆盖），**不是**生效能力编辑器。
  这是契约设计（决议 15 唯一真相），本次只补呈现、不改语义。
- `TypeSafeApiOptions` 不含 `Capabilities`（能力是协议事实），不受本次影响。
- OpenCodeZen 手动构造 schema 且不暴露能力覆盖项（其能力硬编码为 chat），
  因此它的卡片只显示生效能力块，没有能力多选框。
