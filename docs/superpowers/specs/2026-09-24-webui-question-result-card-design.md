# WebUI 问题卡片（结果态）专用展示设计

**日期**：2026-09-24
**分支**：feature/modular-architecture
**状态**：设计定稿 v1（待子代理规格审查签字）
**依据**
- `docs/architecture/01-overview.md`、`02-modules.md`、`04-development-standards.md`
- `docs/superpowers/specs/2026-09-20-question-tool-and-interaction-infrastructure-design.md`（QuestionTool 与交互基础设施；其 §2.2 非目标曾标注"不做已答态回溯 UI"，本设计为该非目标中**结果展示**部分的增量补全）
- `docs/superpowers/specs/2026-09-21-tui-entry-design.md`（TUI 端问答形态）
- 代码基线：`samples/Seeing.Agent.Tui/Rendering/TuiRenderer.cs:555-670`（TUI 答案卡）、`samples/Seeing.Agent.WebUI/Components/Messaging/QuestionCard.razor`（在途表单卡）、`samples/Seeing.Agent.WebUI/Components/Messaging/ToolCallMessageComponent.razor:91-115`（当前通用结果）、`samples/Seeing.Agent.WebUI/Rendering/RenderingServiceExtensions.cs:107-158`（专用卡注册）、`src/capabilities/Seeing.Agent.Tools.Question/QuestionTool.cs:161-189`（输出格式与哨兵）

---

## 1. 问题与动机

### 1.1 需求

WebUI 中 `question` 工具的**结果态**目前以原始文本落进通用工具卡的 `<pre>`，无法体现"提了哪些问题、用户选了哪些选项"的结构。TUI 已实现专用答案卡，WebUI 需补全为同语义的专用卡片。

### 1.2 现状缺口

| # | 事实 | 证据 |
|---|------|------|
| G1 | WebUI **已有**在途提问表单专用卡（单选/多选/文本/其他/必填/取消） | `Components/Messaging/QuestionCard.razor` + `ToolQuestionSlot.razor` + `Services/QuestionInbox*.cs`（槽位 order=110） |
| G2 | WebUI **缺**结果态专用卡：`question` 工具完成后结果落通用 `<pre>` | 结果段 `Components/Messaging/ToolCallMessageComponent.razor:91-115`；`CanRender` 仅排除 task/bash 见 `Rendering/RenderingServiceExtensions.cs:148-151` |
| G3 | `ToolCallViewModel` 无 `IsQuestionTool` 识别属性 | `Models/ToolCallViewModel.cs:83,88,123`（仅 IsTodoTool/IsBashTool/IsTaskTool） |
| G4 | question 工具结果含结构化答案（哨兵包裹 JSON），但 UI 未解析 | `QuestionTool.cs:181-189`（`<<<USER_ANSWERS_BEGIN/END>>>`） |
| G5 | TUI 已有同语义答案卡可作行为对照 | `TuiRenderer.cs:555-630`（`√ 选中项` / `自定义：…`） |
| G6 | WebUI 测试项目**无 bUnit**，组件渲染快照不可行；投影模型单测可行 | `tests/apps/Seeing.Agent.WebUI.Tests/`（无 bunit 引用，有 `BashToolDisplayHelperTests` 等） |

### 1.3 与既有 spec 的关系

2026-09-20 设计将"已答态回溯 UI"列为非目标（决议后卡片从槽位消失、答案经工具结果回流）。本设计**只补结果态的只读展示**，不改变在途交互机制、不做历史交互回溯（不可在此卡上重新作答/编辑）。

---

## 2. 目标与非目标

### 2.1 目标

1. WebUI 中 `question` 工具完成后，渲染**专用结果卡**（区别于通用 `<pre>`）。
2. 结果卡**完整呈现**：每题题干 + 全部选项（选中项高亮/勾选，未选项灰显）+ 自定义答案单独标注。
3. 结果卡**带工具头部**（图标 + 标题 + 状态徽标 + 耗时），视觉与 Task/Bash 专用卡一致。
4. **成功与非成功同卡不同态**：取消/超时/通道不可用/失败显示状态徽标 + 说明文案，不显示答案区。
5. 与在途表单卡解耦：不改动 `QuestionCard`/`QuestionInbox`/槽位机制，不消费 `question.*` 事件。

### 2.2 非目标

- 不重做在途提问表单卡外观，不改其交互与提交链路。
- 不新增后端事件/契约，不改 `QuestionTool` 输出格式，不改 Abstractions 层。
- 不做"已答态回溯交互"（重新作答/编辑答案）——本卡为只读展示。
- 不新增 `ContentBlockType.Question`（继续复用 `ToolCall` 块 + 工具名分派）。
- 不做复制按钮、导出、答案搜索等附加交互（YAGNI）。
- 不改动 CLI/Gateway 宿主。

---

## 3. 术语

| 术语 | 含义 |
|------|------|
| 结果态 | `question` 工具已结束（成功或失败）后的只读展示 |
| 在途态 | `question` 工具执行中、等待用户作答（由 `QuestionCard` 内联槽位负责） |
| 哨兵 | `<<<USER_ANSWERS_BEGIN>>>` / `<<<USER_ANSWERS_END>>>`，包裹结构化答案 JSON |
| 投影模型 | UI 只读视图模型，从 `ToolCallViewModel` 现算（非权威） |

---

## 4. 架构与数据流

### 4.1 渲染路径

```
SessionToolCall("question")
  → ToolCallViewModel.FromSessionToolCall            （已有）
  → ContentBlockBuilder.BuildFromMessage → ContentBlock(ToolCall)
  → MessageRenderPipeline.RenderContentBlocks
  → MessageComponentRegistry.TryGetComponent（按 Priority 升序取首个 CanRender）
      → QuestionMessageComponent   Priority 43       ← 新增，命中
      →（不再到达）ToolCallMessageComponent Priority 50
```

### 4.2 数据来源（零后端改动，全部取自已有 `SessionToolCall`）

| 字段 | 路径 | 用途 |
|------|------|------|
| 入参题面 | `ToolCallViewModel.Parameters`（question JSON：`questions[]` 含 `id/header/question/options[{label,description}]/kind/custom/required/defaultSelectedLabels/defaultCustomAnswer`） | 构建题面与全部选项 |
| 答案 | `ToolCallViewModel.Result`（成功时含哨兵 JSON `[{questionId,selectedLabels[],customAnswer}]`） | 标记选中项、自定义答案 |
| 状态 | `ToolCallViewModel.Status` + `Error` | 判定完成/取消/超时/不可用/失败 |
| 元数据 | `ToolCallViewModel.Metadata["status"]`（成功为 `"completed"`） | 成功态辅助判定 |

### 4.3 与在途槽位的关系（时序）

- 在途：`ToolCall` 块已存在（`Status=pending/running`），其后内联槽位 `QuestionCard` 渲染表单。
- 决议：`QuestionResolvedEvent` → 槽位按 `IsPending` 过滤消失；工具块更新为结果（`Result`/`Status`）。
- 结果：`ToolCall` 块命中 `QuestionMessageComponent`，渲染结果卡。
- 可能存在亚秒级空窗（槽位先消失、工具结果后到），可接受。

**关键约束**：结果卡不在途消费 `PendingChanged`、不订阅任何事件；纯由工具块驱动。

---

## 5. 投影模型

### 5.1 类型定义

新增 `samples/Seeing.Agent.WebUI/Models/QuestionResultCardModel.cs`（仿 `Models/BashCardModel.cs` 的 `From(ToolCallViewModel)` 范式）：

```csharp
public enum QuestionCardState { Completed, Running, Cancelled, Timeout, Unavailable, Failed }

public sealed class QuestionResultCardModel
{
    public required ToolCallViewModel ToolCall { get; init; }
    public QuestionCardState State { get; init; }
    public IReadOnlyList<QuestionResultItem> Items { get; init; }
    public string DisplayTitle { get; init; }        // 等待你的回答 / 已作答 / 未完成作答
    public string StateText { get; init; }           // 提问中 / 完成 / 已取消 / 超时 / 不可用 / 失败
    public string? FailureMessage { get; init; }     // 失败态说明（来自 ToolCall.Error）
    public bool IsRunning => State == QuestionCardState.Running;
    public string TagColor => State switch          // 徽标色单一来源，避免"绿色徽标显示失败"
    {
        QuestionCardState.Completed => "success",
        QuestionCardState.Running => "processing",
        QuestionCardState.Cancelled => "default",
        QuestionCardState.Timeout => "warning",
        QuestionCardState.Unavailable => "default",
        _ => "error"
    };
    public static QuestionResultCardModel From(ToolCallViewModel toolCall);
}

public sealed class QuestionResultItem
{
    public required string Id { get; init; }
    public string Header { get; init; }
    public string QuestionText { get; init; }
    public QuestionKind Kind { get; init; }
    public IReadOnlyList<QuestionResultOption> Options { get; init; }
    public string? CustomAnswer { get; init; }
    public bool IsAnswered { get; init; }
}

public sealed class QuestionResultOption
{
    public required string Label { get; init; }
    public string? Description { get; init; }
    public bool IsSelected { get; init; }
}
```

`QuestionKind` 复用 `Seeing.Agent.Abstractions.Questions.QuestionKind`（`QuestionCardModel` 已引用该命名空间）。

### 5.2 解析规则（`From`）

**状态判定（分支优先级）** — 必须显式覆盖全部已知分支、未匹配一律 `Failed`，**不得依赖枚举默认值**（避免 `cancelled/rejected` 落入 `Running` 导致卡片永久转圈）：

| 判定顺序 | `ToolCallViewModel` 条件 | State |
|---------|--------------------------|-------|
| 1 | `Status` 为 `pending` / `running` | `Running` |
| 2 | `Status == success` 且 `Result` 含可解析的成对哨兵 | `Completed` |
| 3 | `Status == success` 但无哨兵（异常数据） | `Failed`（组件回退显示原文，见 §7.4） |
| 4 | `Status` 为 `cancelled` / `rejected` | `Cancelled` |
| 5 | `Status == failed` | 由 `Error` 文案映射（见 §5.3） |
| 6 | 其它 | `Failed` |

> `Status` 取值参照 `ToolCallViewModel.StatusText`（`success/failed/cancelled/rejected/pending/running`）；loop 取消时 `AgentExecutor` 会把工具终态置为 `cancelled`，必须显式归类。

**题面与答案**

1. **题面**：解析 `Parameters` → `questions[]`（手动 `JsonDocument`，与 `BashCardModel`/TUI 风格一致）；`kind` 字符串映射 `single/multiple/text → QuestionKind`，缺省 `Single`。仅消费 `id / header / question / options[{label,description}] / kind`；`custom / required / defaultSelectedLabels / defaultCustomAnswer` 是 schema 缺省项，结果卡不需要，**不解析**。
2. **答案（成功）**：取哨兵之间 JSON，按 `questionId / selectedLabels[] / customAnswer` 手动解析（字段与 `TuiRenderer.cs:580-600` 一致）。
3. **配对**：对每题，`answer.QuestionId == question.Id` →
   - `Options[i].IsSelected = answer.SelectedLabels.Contains(option.Label)`
   - `CustomAnswer = answer.CustomAnswer`（空白视为 null）
   - `IsAnswered = SelectedLabels.Count > 0 || CustomAnswer 非空`
4. **运行态**：State=`Running`；关注点为等待态呈现，**不构建 `Items`**（组件在运行态不渲染题面，见 §7.1）。

### 5.3 状态判定与文案映射

成功态以 §5.2 状态判定表为准（`Status == success` 且 `Result` 含可解析哨兵）；`Metadata["status"]` 仅作辅助参考，不作为可覆盖状态判定的独立依据。

失败态映射（集中在 `QuestionResultCardModel` 私有常量/方法，注释注明依赖 `QuestionTool.cs:174-176` 文案）：

| `ToolCall.Error` 内容 | State | StateText |
|----------------------|-------|-----------|
| 含"用户取消了作答" | `Cancelled` | 已取消 |
| 含"用户未在限定时间内作答" | `Timeout` | 超时 |
| 含"问题交互通道不可用"（等待中被销毁） | `Unavailable` | 不可用 |
| 含"当前环境不支持交互提问"（无交互宿主预检） | `Unavailable` | 不可用 |
| 含"问题交互通道未接入"（manager 缺失） | `Unavailable` | 不可用 |
| 其它（含参数校验失败等） | `Failed` | 失败 |

> 前三条文案来自 `QuestionTool.cs:174-176`；后两条来自 `QuestionTool.cs:136,140`。三条归一的语义：无宿主 / 未接入 / 通道销毁，对用户都是"当前无法交互作答"。

> 映射失败不影响正确性：任何未匹配的失败一律归 `Failed` + 显示原始 `Error` 文案。

---

## 6. 组件结构与视觉

### 6.1 新增组件

`samples/Seeing.Agent.WebUI/Components/Messaging/QuestionMessageComponent.razor`

参数（与 Task/Bash 卡一致，由注册工厂注入）：`ToolCall`、`Block`、`Context`、`OnToolClick`。

结构：

```
<div class="tool-call-shell question-result-shell">
  <div class="question-result-header" onclick=ToggleExpand>     ← 头部（非运行态可点击折叠）
     Icon(QuestionCircle) + 标题(DisplayTitle) + Tag(StatusTagColor, StateText)
     + 耗时(GetFormattedDuration)
     + @if _card.IsRunning: Spin                                 ← 运行态进度
     + @if !_card.IsRunning: 展开箭头                             ← 仅结果态显示折叠箭头
  </div>
  @if _card.IsRunning:
    <div class="question-result-waiting">等待你的回答…</div>       ← 不渲染题面
  @else if _isExpanded:
    @if _card.State != Completed:                                ← 失败态 / 回退
      @if !string.IsNullOrWhiteSpace(_card.FailureMessage):
        <div class="question-result-failure">@FailureMessage</div>
      @else if !string.IsNullOrEmpty(ToolCall.Result):
        <pre class="question-result-raw">@ToolCall.Result</pre>  ← 无 Error（如无哨兵）兜底
    @else if Items.Count == 0 && !string.IsNullOrEmpty(ToolCall.Result):
      <pre class="question-result-raw">@ToolCall.Result</pre>    ← 成功但题面解析失败，回退原文
    @else:                                                       ← 成功态，完整呈现
      @foreach item in Items:
        <div class="question-result-item">
          <div class="question-result-item-header"><span class="question-result-item-title">@Header</span></div>
          @if QuestionText: <div class="question-result-item-text">@QuestionText</div>
          @if Options.Count>0:
            <ul class="question-result-options">
              @foreach opt:
                <li class="question-result-option @(opt.IsSelected ? "is-selected" : null)">
                  <Icon CheckCircle /> <span>@opt.Label</span>
                  @if Description: <span class="question-result-option-desc">@Description</span>
                </li>
            </ul>
          @if CustomAnswer 非空:
            <div class="question-result-custom"><Icon CheckCircle /> 自定义：@CustomAnswer</div>
          @if !IsAnswered: <div class="question-result-unanswered">未作答</div>
        </div>
```

### 6.2 视觉规范

- 外壳 `question-result-shell`：沿用 `.tool-call-shell` 基础，`border-radius: 8px`，蓝色系渐变底（与 Task 卡 `#f0f7ff` 同系），与 Task/Bash 卡视觉协调。
- **样式自包含（重要）**：`.question-item*` / `.question-option-desc` 等 class 仅定义在 `QuestionCard.razor` 的内联 `<style>` 中、**非全局**；且 `QuestionCard` 在 `IsPending==false` 时 `return`，其 `<style>` 随组件从 DOM 移除。因此新组件**必须自带全部样式**，使用自有前缀（`question-result-item*` / `question-result-option*`）并在组件内 `<style>` 中定义，**不得依赖在途卡样式**。
- 选中项：`Icon CheckCircle` 绿色（`#52c41a`），label 常规色。
- 未选项：`Icon CheckCircle` 灰色（`var(--color-text-tertiary)`），label 次级灰。
- 自定义答案：`Icon CheckCircle` 绿色 + "自定义：" 前缀（与 TUI `[green]√[/] 自定义：` 语义一致）。
- 失败态与状态徽标：徽标颜色由投影 `QuestionResultCardModel.TagColor` 按 `State` 提供（Completed→success、Running→processing、Cancelled/Unavailable→default、Timeout→warning、Failed→error），避免 `success` 无哨兵等异常态出现"绿色徽标显示失败"的语义矛盾；说明文案次级色。

### 6.3 折叠行为

- 头部点击折叠/展开（仅结果态；运行态不显示折叠箭头，固定展示等待态）。
- 初始：成功/失败态**默认展开**（内容即为用户交互结果，通常简短且需直接可见）。
- 运行结束自动展开（若之前未被用户手动折叠）。

---

## 7. 状态与边界

### 7.1 运行态（与内联表单卡共存）

`question` 工具执行中，`ToolCall` 块（`pending/running`）与内联表单卡 `QuestionCard` 同时存在。为避免重复题面：

- `QuestionMessageComponent` 运行态渲染**极简等待态**：头部标题"等待你的回答"+ `Spin`，内容区显示"等待你的回答…"，**不渲染题面与选项**（题面由下方内联表单卡负责）。

### 7.2 完成态

按 §5.2 配对，完整呈现题干 + 全部选项（选中高亮）+ 自定义答案。

### 7.3 失败态

显示头部状态徽标 + `FailureMessage`（`ToolCall.Error`）。区分已取消/超时/不可用/失败（§5.3），不显示答案区。

### 7.4 容错

| 情形 | 行为 |
|------|------|
| `Parameters` 非合法 JSON / `questions` 缺失 | `Items` 为空；成功态若无题面则回退显示 `Result` 原文（`<pre>`），不抛异常 |
| `Result` 无哨兵或 JSON 非法 | 视为非成功 → 按失败态/回退显示原文 |
| 字典/枚举映射异常 | 一律 `Failed`，显示原始 `Error`/`Result` |
| 解析异常 | `From` 内部 catch，绝不向渲染管线抛出 |

### 7.5 边界汇总

| 场景 | State | 展示 |
|------|-------|------|
| 运行中 | Running | 头部 Spin + "等待你的回答…" |
| 成功 · 单选/多选 | Completed | 题面 + 全部选项 + 选中勾 + 自定义 |
| 成功 · 文本题 | Completed | 题面 + 自定义文本答案 |
| 成功 · 未作答题（非必填跳过） | Completed | 题面 + "未作答" |
| 用户取消 | Cancelled | 状态徽标 + "用户取消了作答" |
| loop 取消 / 拒绝（`Status=cancelled/rejected`） | Cancelled | 状态徽标 + 原始 Error（可能为空） |
| 超时 | Timeout | 状态徽标 + "用户未在限定时间内作答" |
| 无交互宿主 / 未接入 / 通道销毁 | Unavailable | 状态徽标 + 原始 Error（"当前环境不支持交互提问…" / "问题交互通道未接入" / "问题交互通道不可用…"） |
| 参数校验失败 / 无哨兵 | Failed | 状态徽标 + 原始 Error / 回退原文 |

---

## 8. 注册与识别

### 8.1 识别属性

`Models/ToolCallViewModel.cs` 新增（与 `IsBashTool` 同风格）：

```csharp
// 是否为 question 提问工具（仅按工具名）
public bool IsQuestionTool => string.Equals(Name, "question", StringComparison.OrdinalIgnoreCase);
```

### 8.2 组件注册

`Rendering/RenderingServiceExtensions.cs` 的 `AddMessageRendering` 中新增 Priority **43**（升序语义：40 Task < 43 Question < 45 Bash < 50 通用；`question` 与 `bash` 工具名互斥，无实际竞争。源码插入位置可紧邻其他工具卡注册，不要求物理紧随 bash）：

```csharp
services.AddSingleton<IMessageComponent>(sp =>
    new DefaultMessageComponent<MessagingComponents.QuestionMessageComponent>(
        ContentBlockType.ToolCall, 43, "Question",
        block => block.Type == ContentBlockType.ToolCall
                 && block.ToolCall != null && block.ToolCall.IsQuestionTool,
        (block, context) => new Dictionary<string, object?>
        {
            ["ToolCall"] = block.ToolCall!,
            ["Block"] = block,
            ["Context"] = context,
            ["OnToolClick"] = context.OnToolClick
        }));
```

### 8.3 通用卡排除（一致性）

`ToolCallMessageComponent`（Priority 50）的 `CanRender` 追加 `&& !block.ToolCall.IsQuestionTool`，与 task/bash 排除保持同一语义，防止优先级漂移导致回归。

---

## 9. 测试策略

测试项目：`tests/apps/Seeing.Agent.WebUI.Tests/`（无 bUnit，只做纯逻辑单测）。

| 测试文件 | 覆盖 |
|----------|------|
| `Questions/QuestionResultCardModelTests.cs` | `From` 成功单选/多选/文本/多题/未作答；自定义答案；失败四态文案映射；`Parameters` 非法；`Result` 无哨兵；运行态 |
| `Questions/QuestionResultCardRegistrationTests.cs` | 构造 `MessageComponentRegistry`（含本题注册），断言 question 块命中 `QuestionMessageComponent`、普通工具仍命中通用卡 |

若注册测试依赖 DI 初始化较重，可退化为直接断言 `DefaultMessageComponent<QuestionMessageComponent>` 的 `CanRender` 委托对 question 块返回 true。

---

## 10. 关键文件清单

| 动作 | 文件 |
|------|------|
| 新增 | `samples/Seeing.Agent.WebUI/Models/QuestionResultCardModel.cs` |
| 新增 | `samples/Seeing.Agent.WebUI/Components/Messaging/QuestionMessageComponent.razor` |
| 修改 | `samples/Seeing.Agent.WebUI/Models/ToolCallViewModel.cs`（`IsQuestionTool`） |
| 修改 | `samples/Seeing.Agent.WebUI/Rendering/RenderingServiceExtensions.cs`（注册 + 通用卡排除） |
| 新增 | `tests/apps/Seeing.Agent.WebUI.Tests/Questions/QuestionResultCardModelTests.cs` |
| 新增 | `tests/apps/Seeing.Agent.WebUI.Tests/Questions/QuestionResultCardRegistrationTests.cs` |

---

## 11. 实施顺序（并行）

```
[基线] QuestionResultCardModel.cs（投影模型，无内部依赖）
   ├─ 并行: ToolCallViewModel.IsQuestionTool        （独立小改）
   ├─ 并行: RenderingServiceExtensions 注册+排除     （独立小改）
   └─ 串行依赖: QuestionMessageComponent.razor（依赖投影模型）
        └─ 依赖: QuestionResultCardModelTests / QuestionResultCardRegistrationTests
```

并行批次：
1. 投影模型 + `IsQuestionTool` + 注册（三处互不冲突，可并行）；注册引用的组件类型需先存在 → 组件空壳先行或以最小实现提交。
2. 组件实现（依赖投影模型）。
3. 测试（依赖模型与注册）。

---

## 12. 风险与决策记录

| # | 风险 / 决策 | 处置 |
|---|-------------|------|
| R1 | 失败态靠 `Error` 文案映射，脆弱 | 集中一处常量并注释依赖；未匹配一律 `Failed`，不影响正确性（仅徽标文案） |
| R2 | 运行态结果卡与内联表单卡可能重复题面 | 运行态选择"极简等待态"，题面独占归表单卡 |
| R3 | 在途卡 order=110 槽位与结果卡可能短暂并存 | 槽位按 `IsPending` 过滤，决议即消失；亚秒空窗可接受 |
| R4 | 成功但题面解析失败 | 回退显示 `Result` 原文，不丢信息 |
| R5 | 无 bUnit，组件视觉无法自动验证 | 明确列为人工验证项（关键位置）；逻辑层由单测覆盖 |
| D1 | 复用 `ToolCall` 内容块 + 工具名分派，不新增块类型 | 与 Task/Bash 一致，改动面最小 |
| D2 | 不消费 `question.*` 事件 | 权威态在工具块，避免双源不一致 |
