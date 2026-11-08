# 思考强度覆盖（Thinking Override）设计

**日期**: 2026-11-08
**状态**: 已确认

## 问题

思考档位（`options.thinking.levels`）目前只有两个写入点：内置能力目录（`catalog.json` + `local.json`）与 Provider 插件静态表。用户在 `/models` 页编辑模型时**完全无法覆盖思考档位**——`ModelOverride` 里没有 thinking 字段，编辑弹窗也无对应控件。结果是远端/目录没声明档位的模型永远进不了 `SessionToolbar` 的思考选择器，而声明错的档位（如只有 `high`/`max` 却缺 `disabled`）也只能改能力目录，不能按模型修。

同时能力补全卡片（`ModelCapabilitiesSettingsCard`）虽有档位表格，但缺**默认档**，且「支持思考 = 关」会被序列化成 `Options = null`，等于**回退下层**而不是显式关闭。

## 目标

1. `/models` 编辑模型弹窗可编辑思考档位，写入用户覆盖层 `ModelOverrides`。
2. 能力补全卡片补「默认档」，并把「支持思考」开关的含义收紧为**显式声明**（关 = 显式关闭，不回退）。
3. 覆盖语义保持稀疏：与基线相同的值不落盘。
4. **不引入 `budgetTokens` 编辑**（该属性已基本不用；仅保留反序列化兼容与既有数据不丢）。

## 非目标

- 不做 per-level 出站 JSON（`emit` / `wire` 永不引入）。
- 不把思考强度挂到 `AgentDefinition`。
- 不改 `LlmService.ApplyThinkingCatalog` 的规范化规则。
- 不改 `ThinkingOptions.Type`（legacy 字段，读旧配置用）。

## 1. 契约层：`ModelOverride.Thinking`

新增 DTO（**不复用** `ThinkingOptions`：后者 `Type` 是 `string = "disabled"` 非空默认值 + `BudgetTokens` legacy，直接复用会让每个覆盖都写出一堆无关键）：

```csharp
/// ModelOverride 内的思考覆盖（三态：null / Supported=false / Supported=true）
public sealed class ModelThinkingOverride
{
    [JsonPropertyName("supported")] public bool Supported { get; set; }
    [JsonPropertyName("default")]   public string? Default { get; set; }
    [JsonPropertyName("levels")]    public List<ThinkingLevel>? Levels { get; set; }
    [JsonPropertyName("interleaved")] public string? Interleaved { get; set; }
}
```

`ModelOverride` 增加 `[JsonPropertyName("thinking")] public ModelThinkingOverride? Thinking { get; set; }`，并计入 `IsEmpty`、`Clone()`（深拷贝 Levels）。

### 三态语义

| 覆盖层取值 | 应用结果 |
|---|---|
| `thinking` 键缺失 / `null` | 不触碰，回退远端/能力目录声明 |
| `thinking.supported = false` | **不声明思考能力**：`Options.Thinking = { Supported = false }`（请求侧清空 `ThinkingEffort`） |
| `thinking.supported = true` | `Options.Thinking = { Supported = true, Default, Levels, Interleaved }` 整块替换 |

### 关闭思考：必须是「关闭档」，不能是 `Supported=false`

`ThinkingEffortKeys.IsSupported` 要求 `Supported=true && Levels.Count>0`。`Supported=false` 会让
`LlmService.ApplyThinkingCatalog` 清空 `ChatRequest.ThinkingEffort`，而 `OpenAiChatClient` 在
`ThinkingEffort` 为空时落进「缺省开启」分支（`commit 42192244`），结果**用户以为关了、实际仍在思考**。

因此「模型级默认关闭思考」的唯一正确表达：

```jsonc
"thinking": {
  "supported": true,
  "default": "disabled",          // 默认档指向关闭档
  "levels": [
    { "key": "disabled", "label": "关闭" },
    { "key": "high", "label": "高" }
  ]
}
```

出站效果：`ThinkingEffort = "disabled"` → `thinking: {type:"disabled"}`（OpenAI 兼容）/ 不发
`thinking` 字段（Anthropic）。会话里用户仍可临时改成 `high` 恢复思考。

`ModelThinkingOverride.CreateOff(existing)` 封装该构造（保留原档位与 `interleaved`，缺失时补关闭档）。

`ModelThinkingOverride.IsOff`（= `Supported && IsOff(Default)`）用于 UI 判别「当前是否默认关闭」。
`ThinkingEffortKeys.IsDefaultOff(thinking)` 是对 `ThinkingOptions` 的同义判定。

### 默认档是「初值」，不是「请求期兜底」

**曾经的问题**：`ApplyThinkingCatalog` 在 `ThinkingEffort` 为空时回落 `thinking.Default`。于是会话里
选「不指定」——它清空 `SelectedThinkingEffort`——和「从没选过」在请求期完全等价，必然被模型默认档
（比如 `disabled`）顶替。用户看到「不指定」，实际发出的是「关闭」。

**现在**：

| 时机 | 行为 |
|---|---|
| 会话**首次**绑定模型（`session.SelectedModel` 原为空） | `ModelManager.ApplyModelToSession` 把模型 `default` 播种进 `SessionData.SelectedThinkingEffort`（仅当该 key 真在档位表内） |
| 换到另一个模型（非首次绑定） | **不**重新播种——会话已有自己的选择，不能被覆盖 |
| 请求期（`ApplyThinkingCatalog`） | **不**回落 `thinking.Default`；空就是空，非法 key 也直接清空（不再静默改写成默认档） |

这样「默认档」只在会话还没有选择的那一刻介入一次，之后 `SelectedThinkingEffort` 是唯一真相。
不需要给会话字段加三态：区别落在「绑定时刻」，而不是「请求时刻」。

副作用（符合预期）：模型配置里改了默认档，对**已存在**的会话不生效——那是会话自己的设置。
要吃到新默认值，在会话里点一次「不指定」再重新选档，或新建会话。

## 2. 应用器 `ModelOverrideApplier`

- `Apply`：`patch.Thinking is not null` → 整块替换（关闭时给 `{Supported=false}` 空壳，levels 置空）。
- `Diff`：把 `desired.Thinking` 与 `baseline?.Options?.Thinking` 规范化后比较；一致则**不写** `patch.Thinking`（沿用稀疏 diff，避免固化目录值）。比较项：`Supported`、`Default`、`Interleaved`、`Levels` 的 `Key`/`Label` 序列。
- `Clone`：`Options` 由浅引用改为深拷贝（`Thinking` + `Levels`）。现状 `Options = config.Options` 会让覆盖/补全写穿到 `_rawCache` 基线，是既有隐患。

## 3. UI：编辑模型弹窗（`ModelsPage.razor`）

新增「思考强度」区（位于「免费模型」之后）：

- **模式**：`RadioGroup` 两选
  - `跟随声明`（默认）：不写 thinking 覆盖
  - `自定义档位`：编辑档位表，默认档由用户选择
- **不设「默认关闭」模式**：把默认档指向关闭档就已经是这个语义，多一个模式只是同一件事的两种写法。
- **关闭档独立一行**：一个 `Switch` 控制「提供/不提供」，code 恒为 `disabled`（不随用户输入变形）；
  其后才是用户自填档位表格（code + 显示名，可增删）。
- **常用档位快速选择**：表格上方一排可点标签，取值来自 `ThinkingEffortKeys.WellKnownLevels`
  （`minimal` / `low` / `medium` / `high` / `xhigh` / `max` / `ultra`，来自服务端
  `reasoning_effort` 校验清单）。点击即加入表格并自动补中文名，已存在的显示 `✓` 且重复点击无副作用。
  code 输入框用 `AutoComplete` 提供同样的候选，同时**仍是自由文本**——不同厂商可能接受清单外的值。
  清单**不包含** `none`：它等同关闭档（`IsOff` 认它），列进强度候选会出现两个关闭 key。
- **不校验可用性**：快速选择只负责填值，某个档位是否真被该模型接受仍由服务端判定
  （写错会 400 `'reasoning_effort' must be one of ...`），UI 不臆断。
- **默认档**：`Select`，候选 = 关闭档（若提供）+ 自填档位 key，含「不指定」；
  选 `disabled` 即「模型默认关闭思考」，会话里仍可临时改回其它档。
- **思考回传字段**：`Input`，占位 `reasoning_content`。
- **打开弹窗**：模式按覆盖层 `thinking` 判定（缺失 → 跟随声明；有覆盖 → 自定义）；
  `disabled` 从档位表里剥离到独立开关，表格只放用户自填项。当前生效声明用灰字只读展示一行。
- **列表提示**：`ModelRow` 增加「思考」列，显示 `n 档` / `默认关闭 · n 档` / `不支持` / `—`。

### 文案分层（防止「默认」撞词）

三层各有一个「默认」，必须用不同说法区分改的是哪一层：

| 层 | 位置 | 文案 | 含义 |
|---|---|---|---|
| 会话 | `SessionToolbar` 思考下拉首项 | **不指定** | 本会话不挑档，交给模型的默认档 |
| 模型 | 编辑弹窗 / 能力卡片的「默认档」 | 默认档（选 `disabled` = 默认关闭思考） | 模型未指定时用哪一档（**只在会话首次绑定模型时作为初值播种**） |
| 模型 | 「跟随声明」模式 | 跟随声明 | 不写用户覆盖，回退远端/能力目录 |

会话层禁止写「默认」（`UiText.ThinkingEffortUnspecified` 有单测守门）：它读起来像在改模型默认档，
实际只清空会话字段。TUI `/thinking` 的同义项文案同步为「不指定，使用模型默认档」。

## 4. UI：能力补全卡片（`ModelCapabilitiesSettingsCard.razor`）

- `支持思考`：`Switch`，语义 = 该模型是否声明思考能力（`Supported`）。开时展开下面三项。
- `关闭档`：`Switch`，控制档位表是否含 `disabled`（固定 code，固定第一行）。
- `默认档`：`Select`，候选 = 关闭档（若提供）+ 自填档位；指向 `disabled` 即「默认关闭思考」。
- `思考内容字段（interleaved）`：`Input`。
- 与编辑弹窗共用同一套快速选择：`WellKnownLevels` 标签 + `AutoComplete` 的 code 输入框。
- 关掉「关闭档」时，若默认档正指向它则自动清空（避免悬空默认值）。
- 文案明确区分：「支持思考=关」= **不声明**思考能力；「默认关闭思考」= 开启 + 关闭档 + 默认档指向关闭档。

## 5. 测试

`tests/spine/Seeing.Agent.Tests/Llm/ModelOverrideTests.cs`：

| 用例 | 断言 |
|---|---|
| `Upsert_Thinking_ReplacesLevels` | 覆盖后 `Options.Thinking.Levels` 与 patch 一致（覆盖目录声明） |
| `Upsert_ThinkingDefaultOff_StillHasLevels` | `CreateOff` 形态 → `Default` 是关闭档、`Levels` 非空、`Supported=true`（**不是** `Supported=false`） |
| `Upsert_ThinkingNotSupported_ClearsLevels` | `{Supported=false}` → 生效配置 `Supported=false` 且 `Levels` 为空 |
| `Upsert_ThinkingSameAsBaseline_NotPersisted` | 与基线同值 → `GetModelOverride(...)!.Thinking` 为 null（diff 不固化） |
| `Upsert_ThinkingRemoval_RestoresBaseline` | 移除覆盖后回退到基线 thinking |
| `Apply_PatchThinking_DoesNotMutateBaseline` | 应用覆盖后 `GetModelBaseline` 对象的 Thinking 未被改动（深拷贝守门） |

`tests/spine/Seeing.Agent.Tests/Llm/ThinkingCatalogNormalizeTests.cs` 补：
`ApplyThinkingCatalog_DefaultOff_EmitsDisabledKey` — 默认档指向关闭档时 `ThinkingEffort == "disabled"`、
`ThinkingBudgetTokens == null`（这是「关闭真的出站」的唯一证据）。

`tests/capabilities/Seeing.Agent.Llm.ModelCapabilities.Tests`：
本地条目 `Supported=false` 覆盖 embedded `supported=true` 时不回落档位（`BuiltinCatalogMerge` 守门）。

## 6. 风险

- **`Supported=false` 与「关闭」混用**：这是最容易被误实现的一点。任何想让用户「关掉思考」的入口
  都必须产出「含关闭档 + 默认档指向关闭档」的声明，写 `Supported=false` 只会让请求不带档位，
  而 OpenAI 兼容出站会缺省 `thinking:{type:enabled}`，表现为关了没关。
- **`Levels` 整块替换 vs Merge 回落**：`BuiltinCatalogMerge` 在 higher 层 `Levels` 为空时回落 lower 层；
  `Supported=false` 分支必须整块生效，否则「撤销思考声明」会被嵌入目录复活。
- **UI 值固化**：打开弹窗即以生效值预填会让 diff 判定「无差异」→ 不落盘（等价于无覆盖），可接受；
  但初始模式必须是「跟随声明」，不写 `supported=true`。
