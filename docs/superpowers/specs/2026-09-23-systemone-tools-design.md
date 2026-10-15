# SystemOne 判别工具包设计（Seeing.Agent.Tools.SystemOne）

**日期:** 2026-09-23
**状态:** 重构方案（替代初版单工具多态设计）
**范围:** 能力包 `Seeing.Agent.Tools.SystemOne`，基于 `Seeing.Agent.SystemOne`（消费 `ISystemOneService`），提供 **1 个混合合并工具 `systemone_ask` + 3 个同类工具 `systemone_noul/choice/score`**，共用同一套 item 形状定义，并配套内嵌 Skill。

---

## 1. 背景与问题（为什么重构）

初版只提供单工具 `systemone_ask(state, questions[])`，其中 `questions` 是**带 `type` 判别的多态对象数组**。实践暴露三类问题：

1. **多态 + 嵌套数组是 LLM 工具调用里最难填的形状**，模型反复报"questions 缺失或不是数组 / state 缺失或为空"。
2. **本项目 prompt 侧把 schema 拍平**：`ToolsPromptSectionContributor`→`DynamicPromptBuilder.BuildToolSection` 只渲染每个参数的 `type + description`，**嵌套 `items` 结构不可见**（`JsonSchemaPromptFormatting.cs`）。
3. **与官方 `typesafe-ai` skill 冲突**：官方示例用 **以 id 为键的 map**（`questions = { "id": Noul(...) }`），与我们的数组形状打架，模型被带偏。

初版还打过"把字符串化的 `questions`/`state` 再解析一次"的容错补丁——**本次重构予以回退**（补丁不解决根因）。

**重构原则：** 用**同类工具**给出无判别、无多态的精确形状；`systemone_ask` 仅作为"单一 shared state 上混合多题型合并成一次请求"的主接口。

---

## 2. 目标与非目标

**目标：**
- 三同类工具（`noul`/`choice`/`score`）：`questions` 为**同类**对象数组，无 `type` 判别、无多态。
- 混合主接口 `systemone_ask`：一次调用、**单一 `state`**，把各题型合并进同一个 SystemOne 请求；其 item 形状 = 三同类 item 的**并集**（多一个 `type`）。
- 三工具与 merge **共用同一套 item 定义**（代码单一来源 + skill 单一表述）。
- `state` 接受**文本字符串**或**结构化对象/数组**；`questions` 接受**对象数组**或其 **JSON 字符串**（schema 用联合类型，服务端归一化还原）。
- `id` 可选（缺省自动生成），答案按 `id` 返回。
- Skill 引导工具选择，并**明确声明不采用官方 skill 的 map 写法**。

**非目标：**
- 不改动 `Seeing.Agent.SystemOne`（仅消费 Abstractions 的 `ISystemOneService`）。
- 不采用官方 `criteria` 多态形状（object/array 随 type 变）——那只是把多态搬家。
- 不做并行调用于 merge 的语义（merge 就是一次合并调用；跨类型并行由模型自行发多个 tool_call，本包不干预）。
- 不做流式、不做自动 confidence 阈值决策。

---

## 3. 架构决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 包名/位置 | `Seeing.Agent.Tools.SystemOne`（`src/capabilities/`） | 对齐 `Seeing.Agent.Tools.*` 命名 |
| 依赖边界 | 仅 Abstractions + `Seeing.Agent.Tools.Support` | 能力包纪律；不引 SystemOne 实现包/Core/Skills |
| 模块 Id | `systemone.tools` | 对齐 `question.tools` |
| 工具数 | 4（三同类 + 一混合） | 同类=可靠；混合=大 state 跨类型省 token |
| `state`/`questions` 类型 | 联合：`["string","object"]` / `["array","string"]` | 实测模型/网关会把嵌套值字符串化；联合类型 + 解析层归一化可同时接受两种形态 |
| `id` | 可选，缺省 `q{n}` | 降低模型负担 |
| item 形状来源 | 单一静态构造器 `SystemOneToolSchemas` | 三工具与 merge 形状一致 |
| 解析器 | `SystemOneToolParser`，按 kind 校验 | 同类工具**已知** kind，无需 item 带 `type` |
| 形态归一化 | 独立组件 `SystemOneJson`（唯一处理"字符串化 JSON"处） | 属正式契约而非隐藏补丁：schema 声明两者皆可，解析层统一还原 |
| Skill | 更新为 4 工具选择指南 + 冲突声明 | 直接消除与官方 skill 的形状分歧 |

---

## 4. 包结构与文件

```
src/capabilities/Seeing.Agent.Tools.SystemOne/
├── Seeing.Agent.Tools.SystemOne.csproj        # EmbeddedResource: Skills/systemone/SKILL.md
├── SystemOneToolsModule.cs                    # ISeeingModule, Id=systemone.tools（注册 4 工具）
├── SystemOneToolKind.cs                       # enum: Ask | Noul | Choice | Score
├── SystemOneToolSchemas.cs                    # 构建参数 JSON Schema（state/questions 联合类型）
├── SystemOneJson.cs                           # 入参 JSON 形态归一化（唯一处理字符串化 JSON 处）
├── SystemOneToolParser.cs                     # DTO + 按 kind 解析/校验；id 可选自动生成
├── SystemOneTool.cs                           # BuiltInToolBase，按 kind 参数化（Id/Description/Schema/Execute）
├── Skills/
│   ├── systemone/SKILL.md
│   └── SystemOneSkillRegistrationHostedService.cs
└── Extensions/SystemOneToolsServiceExtensions.cs

tests/capabilities/Seeing.Agent.Tools.SystemOne.Tests/
├── SystemOneToolsModuleTests.cs
├── SystemOneToolSchemaTests.cs
├── SystemOneJsonTests.cs
├── SystemOneToolParserTests.cs
├── SystemOneToolExecuteTests.cs
├── SystemOneSkillRegistrationTests.cs
├── SystemOneToolsModuleDiTests.cs
└── ScaffoldSmokeTests.cs
```

命名空间沿用工具包约定：`Seeing.Agent.Core.Tools.SystemOne[.Skills|.Extensions]`；csproj `RootNamespace = Seeing.Agent.Tools.SystemOne`；测试命名空间 `Seeing.Agent.Tools.SystemOne.Tests`。

> 迁移：删除初版 `SystemOneAskTool.cs` 与 `SystemOneAskParser.cs`（被 `SystemOneTool.cs` + `SystemOneToolParser.cs` 取代）。

---

## 5. 工具契约

### 5.1 通用参数

所有 4 个工具的顶层参数一致：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `state` | `["string","object"]` | 是 | 被评估内容：文本字符串，或结构化对象/数组（如 `{ "text": "...", "ticket": {...} }`）；也接受结构化值的 JSON 字符串，服务端自动还原。 |
| `questions` | `["array","string"]`（1–20） | 是 | 待评估问题对象数组；也接受该数组的 JSON 字符串，服务端自动还原。 |
| `model` | `string` | 否 | 覆盖 SystemOne 模型。 |

`id`：每项可选；省略时自动生成 `q0/q1/...`；显式重复或与生成值冲突 → 明确错误。

### 5.2 item 形状（单一来源）

**通用 item 字段**
- `id`（string，可选）
- `instructions`（string，必填）

**`noul` item**（`criteriaTrue` / `criteriaFalse` 可选）
```jsonc
{ "id": "urgent", "instructions": "是否表达紧迫性？", "criteriaTrue": "明确时间敏感", "criteriaFalse": "无紧迫性" }
```

**`choice` item**（`options` 必填，2–50）
```jsonc
{ "id": "team", "instructions": "应由哪个团队处理？",
  "options": [ { "label": "billing", "description": "支付/账单" }, { "label": "technical", "description": "故障/集成" } ] }
```

**`score` item**（`levels` 必填，2–10，低→高）
```jsonc
{ "id": "frustration", "instructions": "客户愤怒程度", "levels": ["平静", "不满", "非常愤怒"] }
```

**`ask` item** = 上述并集 + `type`（`noul|choice|score`，必填）；`options`/`levels`/`criteriaTrue`/`criteriaFalse` 按 type 取用。

### 5.3 四个工具

| 工具 Id | `questions` item | 必填字段 |
|---------|------------------|----------|
| `systemone_ask` | `ask` item（含 `type`） | `type`、`instructions`（其余按 type 校验） |
| `systemone_noul` | `noul` item（无 `type`） | `instructions` |
| `systemone_choice` | `choice` item（无 `type`） | `instructions`、`options` |
| `systemone_score` | `score` item（无 `type`） | `instructions`、`levels` |

Description 要点：
- 三同类：明确本题只问该种题型；`questions` 是同类对象数组；一次可多题。
- `systemone_ask`：明确用于**同一份 `state`** 上混合多题型、合并成一次请求；每项须带 `type`；若只用单一题型，优先用对应同类工具。

### 5.4 校验（按 kind）

- `questions` 1–20；每项 `instructions` 非空；`id` 唯一（含自动生成）。
- `noul`：不得出现 `options`/`levels`。
- `choice`：`options` 2–50，label 非空且唯一；不得出现 `levels`。
- `score`：`levels` 2–10，全为字符串；不得出现 `options`。
- `ask`：按每项 `type` 施加上述规则；缺 `type` 或非法 → 明确错误。
- 错误消息附带"正确用法"提示。

### 5.5 映射与执行（共用）

- 组 `SystemOneRequest`：`State` 原样（字符串或结构化值）；`Questions` 为 `id → SystemOneQuestion { Type, Instructions, Criteria }`。
  - `choice` → `Criteria = { label → description }`
  - `score` → `Criteria = levels[]`
  - `noul` → 有 criteriaTrue/False 时 `{ "true":…, "false":… }`，否则 `null`
- 执行期从 `context.Services` 取 `ISystemOneService`；缺失 → Failure。
- 输出：人类可读摘要 + `<<<SYSTEMONE_ANSWERS_BEGIN>>> <answers JSON> <<<SYSTEMONE_ANSWERS_END>>>`（防注入）；`Metadata = { answer_count, model }`。
- 错误：`SystemOneException` → Failure（带 HTTP 状态码）；`InvalidOperationException` → Failure；调用方取消 → 传播。

---

## 6. 模块与生命周期

```csharp
public sealed class SystemOneToolsModule : ISeeingModule
{
    public string Id => "systemone.tools";
    public IReadOnlyList<string> ProvidedTools { get; } =
        new[] { "systemone_ask", "systemone_noul", "systemone_choice", "systemone_score" };
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();
    public IReadOnlyList<string> DependsOn { get; } = new[] { "systemone" };

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new SystemOneTool(SystemOneToolKind.Ask,    sp.GetRequiredService<ILogger<SystemOneTool>>()));
        services.AddSingleton(sp => new SystemOneTool(SystemOneToolKind.Noul,   sp.GetRequiredService<ILogger<SystemOneTool>>()));
        services.AddSingleton(sp => new SystemOneTool(SystemOneToolKind.Choice, sp.GetRequiredService<ILogger<SystemOneTool>>()));
        services.AddSingleton(sp => new SystemOneTool(SystemOneToolKind.Score,  sp.GetRequiredService<ILogger<SystemOneTool>>()));
        services.AddModuleHostedService<SystemOneSkillRegistrationHostedService>();
    }

    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null) return;
        foreach (var tool in services.GetServices<SystemOneTool>())
            await tm.RegisterToolAsync(tool, cancellationToken).ConfigureAwait(false);
    }

    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // 逐个 UnregisterToolAsync(ProvidedTools…)
    }
}
```
入口 `AddSystemOneTools(registry)` 不变（须在 `AddSeeingCore` 前）。

---

## 7. 内嵌 Skill

`Skills/systemone/SKILL.md`（嵌入资源 + hosted service 注册，机制不变）。正文更新为：

1. **工具选择**：
   - 只用一种题型 / 求最稳 → 用对应同类工具 `systemone_noul` / `systemone_choice` / `systemone_score`（`questions` 是**同类对象数组**，无需 `type`）。
   - 同一份 `state` 上混合多题型、想省 token → 用 `systemone_ask` 一次合并（每项带 `type`）。
2. **形状硬性要求**：`state` 可为文本字符串或结构化对象/数组；`questions` 为**对象数组**（若只能给字符串，则给该数组的 JSON 字符串，服务端会自动还原）。二者被字符串化的结构化值都会被还原；`questions` **不是 map**。
3. **冲突声明**：本工具用 `questions` 数组形状；**不要**套用官方 `typesafe-ai` 的 `questions` map（id→题面）写法，也**不要**手写原始 HTTP 调用。
4. 其余最佳实践（批量提问、拆分判断、读 confidence、字段路径引用等）保留。

---

## 8. 测试计划

| 测试类 | 覆盖 |
|--------|------|
| `SystemOneToolsModuleTests` | Id=`systemone.tools`；`ProvidedTools` 含 4 个；`DependsOn=["systemone"]` |
| `SystemOneToolSchemaTests` | 4 工具各自 schema：`state` 为 `["string","object"]`；`questions` 为 `["array","string"]`；required 含 `state`/`questions` |
| `SystemOneJsonTests` | 字符串化 JSON 的递归解包（对象/数组/双层字符串）；普通文本与数字不被改写 |
| `SystemOneToolParserTests` | 同类解析（无 type）；ask 解析（有 type）；`id` 缺省自动生成且唯一；重复 id → 失败；choice options 2–50、重复 label → 失败；score levels 2–10、非字符串 → 失败；noul 带 options/levels → 失败；`state` 字符串/对象均接受；`questions` 及 item/options/levels 的 JSON 字符串形态均可解析 |
| `SystemOneToolExecuteTests` | 4 工具映射到 `SystemOneRequest`（choice→字典、score→数组、noul→criteria/null）；`id` 自动生成出现在请求键；输出定界 JSON + Metadata；无 `ISystemOneService` → Failure；`SystemOneException` → Failure 带状态码；`InvalidOperationException` → Failure；取消传播 |
| `SystemOneSkillRegistrationTests` | 注册内嵌 skill；无 `ISkillManager` 不抛 |
| `SystemOneToolsModuleDiTests` | `AddSystemOneTools` 登记模块；Activate 后 4 工具可查；Deactivate 后全部注销 |

运行（VSTest，**禁用 `dotnet test`**）：
```bash
dotnet build tests/capabilities/Seeing.Agent.Tools.SystemOne.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Tools.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.SystemOne.Tests.dll
```

---

## 9. 依赖与登记

csproj 同初版（Abstractions + Tools.Support + DI/Logging 抽象 + `EmbeddedResource`，无版本号、不引 Core/SystemOne/Skills）；slnx 登记不变。

---

## 10. 验收标准

1. `dotnet build Seeing.Agent.slnx` 通过，0 错误
2. 单测全通过（VSTest）
3. 依赖合规（仅 Abstractions + Tools.Support）
4. 模块 `ProvidedTools` = 4；Activate 后 4 工具均可经 `IToolManager` 查到，Deactivate 后全注销
5. 形态归一化由 `SystemOneJson` 承担（唯一处理"字符串化 JSON"处）；`state` 为 `["string","object"]`、`questions` 为 `["array","string"]`，解析层同时接受字符串与结构化形态
6. 三同类工具 `questions` item **不含** `type`；`systemone_ask` item **含** `type`
7. 内嵌 Skill 已更新为 4 工具选择指南 + 冲突声明

---

## 11. 修订（2026-09-24）：入参形态归一化

**背景：** 目标是让入参同时接受 `string` 与 `object`，而非把嵌套值一律字符串化。实测显示模型/网关会稳定地把 `state`/`questions` 输出为字符串（内容仍是合法 JSON），仅靠 schema 类型声明无法约束。

**修订：**

1. 新增 `SystemOneJson`（`internal static`），是全工具**唯一**处理"字符串化 JSON"的地方：
   - `Unwrap`：字符串内容是合法 JSON（对象/数组/字符串）时递归向内解包（限深 8 层）；普通文本与数字/布尔等标量保持原样。
   - `TryGetObject` / `TryGetArray` / `NormalizeState`：面向调用点的语义化入口。
2. Schema 声明联合类型：`state` = `["string","object"]`、`questions` = `["array","string"]`（`JsonSchemaPromptFormatting` 会把联合渲染为 `string|object`，安全）。
3. `SystemOneToolParser` 改为全部经由 `SystemOneJson` 取结构化值：`state`（字符串或对象）、`questions`、每个 item、`options`、`levels` 均可为原生结构或其 JSON 字符串。
4. 与第 1 节"初版容错补丁已回退"的关系：本修订**不是**撤销决定，而是把"再解析一次"升级为**设计层面的形态归一化**——单一职责组件、schema 显式声明两者皆可，属正式契约而非隐藏兜底。

**验证：** `tests/capabilities/Seeing.Agent.Tools.SystemOne.Tests` 52 项全通过（含 `SystemOneJsonTests` 与新增字符串化用例）。
