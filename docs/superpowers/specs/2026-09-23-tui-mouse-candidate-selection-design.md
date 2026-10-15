# Seeing.Agent TUI 候选鼠标/键盘选择设计（seeing-tui）

**日期**：2026-09-23
**分支**：master
**状态**：**v2.1 已实现**（v2 吸收设计/架构双审全部 BLOCKER/MAJOR；v2.1 吸收集成 code review M-1~M-4/m-2/m-3，见 §14；全量单测绿）

> **v2 修订摘要**（对应两份终审）：① **DSR 光标报告与鼠标事件严禁进按键通道**——DSR 走独立 `TuiAnchorProbe` 旁路（B-1）；② 下拉优先路由**前置于 `_editor.Apply`**，被拦键不 Apply、不重置 `_cancelArmedAt`、非命中/无下拉的 Mouse 早退（M-1/MAJOR-1/m-2）；③ 选择态（候选集+游标+勾选+可见）纳入 `TickRenderSnapshot` 且变更即强绘（M-2）；④ DSR 底锚与命中表**同帧代次配对**，跨代次/未校准安全失效（M-3）；⑤ 候选行强制 `DisplayText.Width` 单物理行截断、命中表只含实画行（M-5/M-6）；⑥ 内联补全**不翻页**（端点停），模态才翻页，消除预截断冲突（M-7/MINOR-5）；⑦ 模态擦画口径与 Panel 边界明确（M-8）；⑧ **§5 冻结契约**前置 Batch 0（M-9）；⑨ `MouseEnabled` 定 `TuiCliOptions`、`RawInputReader` 注入、DSR/上报受其门控（MINOR-3/m-5）；⑩ 关闭序列去 `?25h`（MINOR-4）；⑪ `ITerminalSurface.PromptListAsync` 改名避重载、`FakeTerminalSurface` 同步（NIT-3）；⑫ 废弃 Tab 模态分支与 `CompletionCancelSentinel`/`PromptChoiceAsync`（m-1）；⑬ 鼠标解码先判 `0x40/0x20`、忽略 release（m-4）；⑭ resize 失效底锚（m-3）。

**一句话**：为 seeing-tui 三类「候选选择」（斜杠补全、问答单选/多选、权限审批）统一加入**键盘上下选择（默认第一项、Enter 确认）**与**鼠标 hover 高亮 + 点击确认**；斜杠补全为**打字即内联下拉**；鼠标**全程常开、可配置关闭、终端不支持自动降级为纯键盘**。

---

## 0. 背景与现状（代码基线，已核对）

依赖代码：`samples/Seeing.Agent.Tui/`。

- **输入链**：`RawInputReader`（raw stdin + bracketed paste）→ `TuiRawInput` → `InputKeyDecoder.Decode` → `TuiKeyInput` → `TuiInputThread` → `TuiPromptInputRelay`（按「提示是否活跃」分发到 `_engine`/`_prompt`）。**无鼠标处理**，未启用鼠标上报。`DrainLocked` 把 `ESC[<…M` 归 `TuiRawEscape`（`RawInputReader.cs:257-276、431-445`）。
- **斜杠补全**：`TuiCompletionProvider` 产候选；`RenderAsync` 已把 `GetCompletionCandidates()`（截断至 `MaxCompletionRows=8`，`TuiChatEngine.cs:1662-1671`）传渲染器，`TuiRenderer` 在输入行**上方**内联渲染候选（`TuiRenderer.cs:82-83、174-186`）——**无高亮游标**。`BuildCompletions` 以 `Name.Length`（char 数非显示宽）对齐、不截断（`TuiRenderer.cs:176-180`）。`Tab` → `HandleCompletionAsync`：唯一项 `TryApply` 直补 / 多候选 `PromptChoiceAsync`→Spectre `SelectionPrompt` 模态（`TuiChatEngine.cs:857-875、949-984`），含 `CompletionCancelSentinel`（`:41`）。
- **候选上下键现状**：`InputKeyDecoder` ↑/↓ → `HistoryPrev/Next`（`:21-22/55-56`）；`TuiInputEditorState.Apply` 翻历史（`:63-69`）。补全态未拦截。
- **`HandleInputAsync`**：第 `402-403` 行在 switch **之前**无条件 `_editor.Apply(key)`；未匹配 Action 落 `default`（`:453-456`）执行 `_cancelArmedAt=null; _lastRenderAt=DateTime.MinValue`。
- **渲染节流/脏检查**：`MaybeRenderAsync`（`:1486-1505`）按 `CountActiveChars` + `TickRenderSnapshot`（`:1519-1528`）判定；后者含 width/height/`_terminalCommits`，**不含候选/选择态**。
- **问答**：`QuestionPrompt` 单选=`SelectionPrompt<string>`、多选=`MultiSelectionPrompt<string>`、文本=`TextPrompt<string>`（`AllowCustom` 选「其他（自定义）」续 `AskCustomAsync`）。**权限**：`PermissionPrompt`=`SelectionPrompt<Choice>`，`Panel` 概要后画。
- **模态输入桥**：`SpectreTerminalSurface.PromptAsync` 在渲染线程跑委托；Spectre 提示经 `InputOverrideConsole`→`ChannelAnsiConsoleInput`（绑 `_prompt.Reader`）读键，`TuiKeyInput→ConsoleKeyInfo` 有损映射（丢鼠标）。`_prompt` 通道 `SingleReader=false`（`ChannelAnsiConsoleInput.cs:256-260`）。
- **光标几何**：`TuiCaret.RowsBelow`=插入点相对**活动区末行**上移行数；`TuiCaretSequences.Place/Restore` 用 `CUU/CUD/CR/CUF`。渲染序：`ctx.UpdateTarget(view)`→`PlaceCaret(caret)`（`SpectreTerminalSurface.cs:375-376`），即 `PlaceCaret` 会把光标从末行移到插入点。`TailClipRenderable` 保尾裁剪（`maxLines`）。`?25h` 由 `SpectreTerminalSurface.RestoreTerminal` 写（`:560-569`），`?2004l` 由 `RawInputReader.StopAsync` 写（`:136`）。

## 1. 目标 / 非目标

**目标**
1. 斜杠补全：打字即内联下拉，**默认高亮第一项**；↑/↓ 移动游标（不翻历史），Enter/Tab **接受高亮项带入输入框**（光标移到命令名后空格处，不提交）；继续打字实时更新并复位游标；鼠标 hover 高亮、点击＝接受带入；Esc 先收起下拉（下拉可见时），再按才走原取消语义。
2. 问答单选/多选、权限审批：**保留模态**，换成自绘列表控件，键盘上下/Enter/Esc（多选 Space 切换、Enter 提交）+ **鼠标 hover 高亮 + 点击**（多选点击切换 ☐/☑）。
3. 鼠标上报**全程常开**，hover 开启；**可配置关闭**；非交互/不支持终端**自动降级**为纯键盘。
4. 键盘是全功能主路径；鼠标是**纯增量**（关鼠标后键盘体验不回退）。

**非目标**
- 不引入 alternate-screen 整屏接管。
- 不改补全候选数据源/命令表/`ICommandRegistry`。
- 不改问答/权限**业务语义**（scope 过滤、多选必填性、`AllowCustom` 续接、默认值、截断、取消哨兵）——仅替换「列表呈现 + 选择输入」承载。
- 不做拖拽框选、滚轮翻页（滚轮列 P3 可选）。
- 不向 `src/` 脊柱/能力包加任何类型；全部落 `samples/Seeing.Agent.Tui/`（组合根）。

## 2. 关键决策（用户已确认）

| 维度 | 决策 |
|------|------|
| 补全形态 | 打字即**内联下拉**（非 Tab 才弹） |
| 鼠标覆盖 | 补全 + 问答 + 权限**三类全覆盖** |
| hover | **要**悬停高亮（需 any-motion 上报） |
| 启用策略 | **全程常开 + 可关 + 不支持自动降级** |
| 路线 | **方案 C：分层复用**（单一 `SelectableList` 内核 + 鼠标/命中共享层；补全=内联锚定，问答/权限=模态锚定） |
| 命中锚点 | **DSR `ESC[6n` 经 `TuiAnchorProbe` 旁路校准 + 同帧代次配对**；不用 alternate-screen |
| 内联分页 | **不翻页**（候选已 ≤`MaxCompletionRows`，↑/↓ 端点停）；模态按各自 PageSize 翻页 |

## 3. 架构分层

```
层1 输入解码   RawInputReader: 启用鼠标上报 + 解析 SGR 鼠标(TuiKeyInput 旁→通道) / DSR 回复(→TuiAnchorProbe 旁路, 不进按键通道)
              InputKeyDecoder: 鼠标→TuiKeyInput(Mouse)；DSR 不经此
层2 命中几何   TuiAnchorProbe 持「参考行绝对位置 + 代次」；渲染帧随附 CompletionHits(ItemIndex,RowsFromBottom,frameGen)
              TryHitTest(probe,hits,absRow)→itemIndex（纯函数）
层3 选择内核   SelectableList：items + selectedIndex(+multi: checked) + allowPaging/pageSize/页窗
层4 承载适配   ① 内联下拉(补全，活动区渲染，引擎持 completion SelectableList 态)
              ② TuiListPrompt(问答/权限，自绘模态，直读 TuiKeyInput 含鼠标，经 PromptListAsync/TuiPromptContext)
层5 配置降级   TuiCliOptions.MouseEnabled(默认 true)+--no-mouse；关闭/降级 → 层1不启用上报、探针不请求、不产鼠标事件，键盘全功能
```

**Spectre 依赖变化**：问答/权限弃用 `SelectionPrompt`/`MultiSelectionPrompt`，改自绘 `TuiListPrompt`；仍用 Spectre 于文本答案 `TextPrompt`、Markdown、Panel、`Live`。**模态自绘控件不再经 `ConsoleKeyInfo` 桥**，直接消费 `_prompt.Reader` 的 `TuiKeyInput`（含鼠标）；`ChannelAnsiConsoleInput` 仅继续服务文本 `TextPrompt`。

## 4. 输入解码：鼠标上报与解析

### 4.1 上报模式（启用/关闭）

`RawInputReader.StartAsync`（当 `MouseEnabled`）在 bracketed paste 后追加 `\x1b[?1000;1003;1006h`（press/release + any-motion + SGR 坐标）。`StopAsync` 写 `\x1b[?1006;1003;1000l`（与 `?2004l` 同处，**不写 `?25h`**——归 `SpectreTerminalSurface`）。`MouseEnabled=false`/降级时**不写任何鼠标序列** → 终端把鼠标交还用户（选中复制不需 Shift）。

### 4.2 SGR 鼠标序列解析

`DrainLocked` 的 CSI 分支中，识别 `ESC [ < … (M|m)`（`<` 为参数字节后的私有前缀），产 `TuiRawMouse`。解码（**严格按序**）：

```
b = btn; has 0x40 → wheel(b==64?WheelUp:WheelDown)，phase=Press（忽略 m）
   else has 0x20 → phase=Motion，button=按下键(低3位,可为None)
   else → button=低3位映射(0=Left,1=Middle,2=Right,3=None)，终字节 M=Press / m=Release
```

- **点击确认只认 Press**，忽略 Release（`m`），防 press+release 双触发。
- 无法解析的鼠标序列丢弃（`None`），绝不落 `InsertText`。

### 4.3 DSR 光标报告（旁路，不进按键通道）

`RawInputReader` 识别 `ESC [ row ; col R` → **直接调用注入的 `TuiAnchorProbe.Report(row)`**，**不**产出 `TuiRawInput`/`TuiKeyInput`、**不**写 `_engine`/`_prompt` 通道（消除 B-1 自激）。`RawInputReader` 经构造参数持 `TuiAnchorProbe?`（关闭鼠标时可为 null）。

### 4.4 TuiKeyInput 扩展（仅鼠标经此）

`TuiInputAction` 增 `Mouse`；`TuiKeyInput` 增可选 `TuiRawMouse? Mouse`（通道类型仍单一 `TuiKeyInput`）。`InputKeyDecoder`：`TuiRawMouse` → `new TuiKeyInput(TuiInputAction.Mouse, Mouse: raw)`。`ChannelAnsiConsoleInput.MapLocked`：`Mouse` 显式 `return`（不喂 Spectre）。

## 5. 冻结契约（Batch 0 一次性定义，后续各 Agent 只读、不得擅改签名）

> 目的：消除 M-9 并行打架。以下类型/签名由 Batch 0 落地为**新文件**（`samples/Seeing.Agent.Tui/`），编译通过；Wave 各 Agent 按此实现行为与测试。

```csharp
// —— Input/TuiInputTypes.cs（扩展）——
public enum TuiInputAction { /* 原值… */ Mouse }
public enum TuiMouseButton { None, Left, Middle, Right, WheelUp, WheelDown }
public enum TuiMousePhase  { Press, Release, Motion }
public sealed record TuiRawMouse(TuiMouseButton Button, TuiMousePhase Phase, int Col, int Row) : TuiRawInput;
public readonly record struct TuiKeyInput(
    TuiInputAction Action, string Text = "", bool IsEscape = false, TuiRawMouse? Mouse = null);

// —— Input/TuiAnchorProbe.cs（新增）：DSR 底锚，单写者串行、发一个收一个 ——
public interface ITuiAnchorProbe { void Report(int row); bool TryGet(out int row, out long gen); }
public sealed class TuiAnchorProbe : ITuiAnchorProbe {
    public void Invalidate();                 // resize 等：置未知
    public long BeginProbe(Action writeDsr);   // 递增代次、登记 pending、调用 writeDsr 写 ESC[6n；返回该帧 gen
    // Report 由 RawInputReader 收到 ESC[..R 时调用；TryGet 供命中方读最新(行,代次)
}

// —— Rendering/TuiHitRegion.cs（新增）——
public readonly record struct TuiHitRegion(int ItemIndex, int RowsFromBottom, long FrameGen);

// —— Rendering/TuiActiveView.cs（TuiCaret.cs 内）扩展：活动区帧携带命中表——
public readonly record struct TuiActiveView(IRenderable View, TuiCaret? Caret,
    IReadOnlyList<TuiHitRegion> CompletionHits, long FrameGen);

// —— Input/SelectableList.cs（新增，纯逻辑）——
public sealed class SelectableList {
    public SelectableList(IReadOnlyList<string> labels, int pageSize, bool multiSelect = false, bool allowPaging = true);
    public int Count { get; } public int SelectedIndex { get; } public bool MultiSelect { get; }
    public IReadOnlyList<int> PageIndices();                 // 当前可见下标
    public void MoveUp(); public void MoveDown();            // allowPaging=false 时端点停、不滚页窗
    public bool SetByHit(int index);                          // 越界 false
    public void Toggle(int index); public bool IsChecked(int index);
    public IReadOnlyList<int> CheckedIndices();
    public void Reset(int selectedIndex = 0);                // 集合变化→复位游标(不越界则保留)
    public void UpdateItems(IReadOnlyList<string> labels);
    public int ConfirmIndex(); public IReadOnlyList<int> ConfirmChecked();
}

// —— Rendering/Prompts/TuiListPrompt.cs（新增，自绘模态控件）——
public static class TuiListPrompt {
    public static Task<int?> SelectAsync(TuiPromptContext ctx, string title, IReadOnlyList<string> labels,
        int pageSize, int? cancelIndex, CancellationToken ct);
    public static Task<IReadOnlyList<int>?> MultipleAsync(TuiPromptContext ctx, string title, IReadOnlyList<string> labels,
        int pageSize, IReadOnlyList<int>? preChecked, bool notRequired, int? cancelIndex, CancellationToken ct);
    // 多行头部（如权限 Panel 概要）经 ctx.WriteHeader(IRenderable) 先画一次，列表在其下原地刷新
}

// —— Rendering/TuiPromptContext.cs（新增）——
public sealed class TuiPromptContext {
    public required Spectre.Console.IAnsiConsole Console { get; init; }
    public required System.Threading.Channels.ChannelReader<Seeing.Agent.Tui.Input.TuiKeyInput> Keys { get; init; }
    public required ITuiAnchorProbe Anchor { get; init; }
    public bool MouseEnabled { get; init; }
    public void WriteHeader(Spectre.Console.Rendering.IRenderable r); // 模态顶部概要，一次性画
    public Task<int?> RunListAsync(string title, IReadOnlyList<string> labels, int pageSize,
        int? cancelIndex, IReadOnlyList<int>? preChecked, bool multi, bool allowPaging, CancellationToken ct); // 控件内部用
}

// —— Rendering/ITerminalSurface.cs（新增方法，不改既有 PromptAsync）——
Task<T> PromptListAsync<T>(Func<TuiPromptContext, CancellationToken, Task<T>> prompt, CancellationToken ct = default);

// —— Input/TuiPromptInputRelay.cs（新增属性）——
public System.Threading.Channels.ChannelReader<TuiKeyInput> PromptReader => _prompt.Reader;

// —— Input/RawInputReader.cs（构造签名）——
public RawInputReader(bool mouseEnabled = false, ITuiAnchorProbe? anchorProbe = null);
```

`TuiAnchorProbe` 语义：`BeginProbe` 返回 `gen` 并写 `ESC[6n`；随后 `RawInputReader.Report(row)` 落 `(row,gen)` 到 `Latest`（pending 空则丢弃）。因单写者渲染线程「发一个、收一个才发下一个」，pending 唯一、无并发错配。

## 6. 承载 ①：斜杠补全内联下拉

### 6.1 状态归属
引擎持 `_completionSelector`（`SelectableList?`，`allowPaging:false`、`pageSize=候选数≤MaxCompletionRows`）。候选源**沿用** `GetCompletionCandidates()`（截断 8）。**下拉可见判据** = 候选非空（行首 `/xxx` token 且无空格）。渲染数据源与选择器数据源**同源同序**（同为截断后列表）。

### 6.2 输入路由优先级（**前置于 `_editor.Apply`**，TuiChatEngine.cs:403 之前）

`HandleInputAsync` 顶部先做：

```
if (_completionSelector is { Count: > 0 })  // 下拉可见
    switch key.Action:
        Up/Down(HistoryPrev/Next) → selector.Move{Up,Down}; ForceRender(); return false;   // 不 Apply、不翻历史
        Submit / Complete          → AcceptCompletion(); return false;                      // 不 Apply（Apply 不执行 Enter 的 PushHistory）
        Cancel                      → _completionSelector = null; ForceRender(); return false; // 仅收起，不清空输入/不改执行/不重置武装之外逻辑
        Mouse                       → HandleCompletionMouse(key.Mouse); return false;        // hover→SetByHit+ForceRender；Left-Press 命中→AcceptCompletion
        其它(InsertText/Backspace/Move/...) → 落到下方常规：_editor.Apply → 候选重算 → 见 6.2.1
// 下拉不可见：
if key.Action==Mouse → return false;   // 无操作；**不得**重置 _cancelArmedAt、不得设 _lastRenderAt（MAJOR-1/m-2）
… 其余走现有逻辑（_editor.Apply 前置记录 editorWasEmpty 保持原位）
```

- **6.2.1 集合变化复位**：任何改变 `_editor.Text/Cursor`（致候选集变）的按键，在 `Apply` 后重算候选；非空则 `UpdateItems`（游标复位 0）+ 保持可见；空则 `_completion=null`。
- `ForceRender()`：置 `_lastRenderAt = DateTime.MinValue` 并**把选择态计入 `TickRenderSnapshot`**（§6.4）。
- **被拦的 Up/Down/Enter/Tab/Esc/Mouse 一律不调用 `_editor.Apply`**（否则 ↑ 污染历史、Esc 清空输入、Enter 误推历史）。

### 6.3 接受带入
选中项 `name`（含 `/`）：复用 `TuiCompletionProvider` 的 tokenStart/remainder 切分，`_editor.SetTextAndCursor(text[..tokenStart]+name+" "+remainderTrimStart, tokenStart+name.Length+1)`，收起下拉（`_completionSelector=null`），不提交。

### 6.4 渲染与命中（改 `TuiRenderer` + 引擎）
- `BuildCompletions(items, selectedIndex, pageFirst)`：高亮行 `[blue on grey]▸ name  desc[/]`（`▸ `+name 用 `DisplayText.Width` 计宽），**每候选单物理行**：`name`/`desc` 各按显示宽预算、超则截断加 `…`，杜绝折行。页窗：内联全显（≤8）。
- 渲染线程产出 `CompletionHits`：仅**本帧实画**且 `RowsFromBottom ≤ maxLines-1`（同 `ResolveCaret` 裁剪口径，`TuiRenderer.cs:119-129`）的候选项，各含 `{ItemIndex, RowsFromBottom, FrameGen}`。
- 引擎 `RenderAsync`：帧 `gen = probe.BeginProbe(()=>surface 写 ESC[6n})`；hits 带该 `gen` 存入 `_lastCompletionHits`。
- **DSR 写点**：`SpectreTerminalSurface` 在 `ctx.UpdateTarget(view)` **之后、`PlaceCaret(caret)` 之前**写 `ESC[6n`（此时光标在末行）。该 `ESC[6n` 走 `WriteRaw`，不入 Live 擦除记账。仅 `MouseEnabled` 时写。
- 命中：`TryHitTest(probe,lastHits,absRow)`：`probe.TryGet(out anchorRow, out gen)`，**仅当 `gen == 命中表 FrameGen`** 才 `itemRow=anchorRow−hit.RowsFromBottom`，相等→该项；否则 null（未校准/跨帧→键盘兜底，M-3）。resize（`TickRenderSnapshot` 宽高变）→ `probe.Invalidate()`。

### 6.5 选择态纳入脏检查（M-2）
`TickRenderSnapshot` 增字段：`CompletionSignature`（候选串拼接 hash + `SelectedIndex` + 可见 bool）。`TakeTickSnapshot` 采集；空闲 tick 亦能感知游标/勾选变化并补绘。任何改选择态的输入路径同时 `ForceRender()`。

## 7. 承载 ②：模态列表控件 TuiListPrompt（问答/权限）

### 7.1 控件与刷新口径
- `TuiListPrompt` 内部 `SelectableList`（`allowPaging:true`）；每帧：擦旧 → 画新（标题 + `PageIndices` 项，高亮 `▸`/反色，多选前缀 `[☐]/[☑]`）。
- **擦画口径（M-8）**：控件只拥有「列表区」行（Panel 概要之上、由 `WriteHeader` 一次性画的除外）。每帧 `CUU(min(已画行数, 到列表首行的距离))` 回到列表首行，`ESC[J` 擦到屏幕尾后重画；记录 `prevListRows`，确保新帧行数增减都覆盖，且**上移不越过 header/Panel 边界**。
- **命中**：帧尾 `probe.BeginProbe` 写 `ESC[6n`（光标在列表末行时），hits 为 `{ItemIndex, RowsFromBottom(相对列表末行), gen}`；鼠标经 `ctx.Keys` 读入 → hover `SetByHit`、Left-Press 命中→`Confirm`（多选→`Toggle`）。Enter→`Confirm`；Space(多选)→`Toggle`；↑/↓→`Move`；Esc→返回 `cancelIndex`。
- 未校准/跨代次 → 鼠标命中失效，键盘可用。

### 7.2 调用方迁移（对外签名不变，只换内部承载；业务语义不变）
- `QuestionPrompt.ShowAsync(surface, request, ct)` 签名不变；内部 `AskSingleAsync`：`SelectionPrompt`→`surface.PromptListAsync` 内 `TuiListPrompt.SelectAsync`；`selected==cancel`→null；`==OtherChoice`→续 `AskCustomAsync`（仍 `TextPrompt`）；否则 `SelectedLabels=[labels[selected]]`。`AskMultipleAsync`→`MultipleAsync`（下标集→labels，含 OtherChoice 续自定义，`Required/NotRequired` 保持）。`AskTextAsync` **不变**。
- `PermissionPrompt.ShowAsync` 签名不变：`ctx.WriteHeader(BuildSummary Panel)` 一次性 → `TuiListPrompt.SelectAsync(choices labels, cancelIndex=Choice.Cancel 下标)` → 下标回 `Choice`。

### 7.3 surface 接线
- `SpectreTerminalSurface.PromptListAsync`：`RunPrompt` 前构造 `TuiPromptContext{ Console=_console, Keys=_inputRelay.PromptReader, Anchor=_anchor, MouseEnabled=_mouseEnabled, … }` 交委托；渲染线程单写者不变。需向 surface 注入 `TuiPromptInputRelay.PromptReader` 与 `TuiAnchorProbe`（`Program.cs` DI 接线）。
- `FakeTerminalSurface` 增 `PromptListAsync`（回放预置选择/读 fake keys）。
- 文本答案路径仍 `PromptAsync`+`ChannelAnsiConsoleInput`；同一模态时刻二者互斥（`BeginPrompt` 排空防串扰）。`PromptPendingAsync`（TuiChatEngine.cs）的 `BeginPrompt/EndPrompt` 包裹保持。

## 8. 配置与降级

- `TuiCliOptions`（record）增 `MouseEnabled`（默认 true）；`--no-mouse`（`TuiCliOptions.Parse`）置 false。
- 接线：`RunAsync(options)`→`RunLoopAsync`→`new RawInputReader(options.MouseEnabled, _anchorProbe)`；`_anchorProbe` 为引擎持有的 `TuiAnchorProbe` 单例；surface/renderer 探针写点受 `MouseEnabled` 门控。
- **自动降级**：非 TTY 已在 `RunAsync` 前置退出（TuiChatEngine.cs:173-177）。`MouseEnabled && TTY` 才启用上报。无 SGR 终端：写了开关亦不产事件（键盘可用，关闭序列无害）；DSR 不回→`probe` 恒未校准→鼠标命中失效、键盘不受影响、不崩不残留。

## 9. 可选增强（P3）
滚轮 `WheelUp/Down`→翻页/移动游标；补全「接受」额外触发键（行尾 Right/End）。

## 10. 兼容与回归风险

| 风险 | 处置 |
|------|------|
| 常开鼠标致终端选中复制需 Shift | §8 `--no-mouse`/降级；README 注明 |
| DSR 回灌按键流致满帧自激 | §4.3 DSR 走 `TuiAnchorProbe` 旁路，**不进 relay/engine**（B-1） |
| 无下拉时 Mouse/hover 冲掉执行中 Esc 二次确认 & 绕过节流 | §6.2 Mouse 非模态无操作、不重置 `_cancelArmedAt`、不设 `_lastRenderAt`（MAJOR-1/m-2） |
| hover/方向键改游标不重绘→高亮错位 | §6.5 选择态入 `TickRenderSnapshot` + 变更即 `ForceRender`（M-2） |
| DSR 底锚×命中表帧错配 | §6.4 `(row,gen)` 与 hits `FrameGen` 同代次才命中，否则失效（M-3） |
| 候选折行/CJK 破坏逐行几何 | §6.4 `DisplayText.Width` 单物理行截断（M-5） |
| TailClip 裁掉候选致命中错位 | §6.4 命中表只含实画行（同 ResolveCaret 口径，M-6） |
| 内联预截断 vs 翻页矛盾 | §2/§6.1 内联不翻页、端点停（M-7/MINOR-5） |
| 模态自绘擦画残影/啃 Panel | §7.1 擦画口径、header 一次性、上移不越界（M-8） |
| 跨 Agent 契约漂移 | §5 冻结契约、Batch 0 前置（M-9） |
| Tab 模态与内联下拉二义 | 废弃 `PromptChoiceAsync`/`CompletionCancelSentinel`，Tab=带入（m-1） |
| 下拉拦截键误 `Apply`（翻历史/清空/推历史） | §6.2 优先路由**前置于 `_editor.Apply`**、被拦键不 Apply（M-1） |
| 中文/markup | 控件标题/项沿用 `Markup.Escape`；`▸`/`☐☑` 计显示宽 |

## 11. 测试策略（VSTest；命名 `{方法}_{场景}_Should{预期}`/AAA）

`tests/apps/Seeing.Agent.Tui.Tests/`：
- **RawInputReader**：SGR `ESC[<0;10;5M`/`ESC[<0;10;5m`(忽略)/`ESC[<32;10;5M`(Motion)/`ESC[<64;..M`(WheelUp) → 期望 `TuiRawMouse` 且解码序正确；`ESC[8;20R` → 调 `probe.Report(8)`、**不产按键**；鼠标/paste/其它 CSI 混排不误判。
- **InputKeyDecoder**：`TuiRawMouse`→`TuiKeyInput(Mouse)`；`ChannelAnsiConsoleInput.MapLocked(Mouse)` 不产 `ConsoleKeyInfo`。
- **SelectableList**：默认游标 0；`allowPaging:false` MoveDown 端点停；翻页模式页窗滚动；SetByHit 越界 false；Toggle/CheckedIndices；UpdateItems 复位。
- **TryHitTest**：同代次命中/跨代次失效/未锚定失效；`anchorRow−RowsFromBottom` 换算；越界 maxLines 项不在表内。
- **引擎路由（可提纯决策函数）**：下拉可见时 ↑/↓=游标不翻历史、Enter=带入不提交不推历史、Esc=收起不清空、Mouse 命中=带入、Mouse 无命中/无下拉=**不重置 `_cancelArmedAt`**；隐藏时回归原行为。
- **接受带入**：tokenStart/remainder 切分 + 光标位。
- **TuiListPrompt**（仿 `QuestionPromptTests`，真实 `_prompt` 通道喂 `TuiKeyInput` 含鼠标）：键盘确认/取消哨兵/多选取舍/点击确认/hover 改游标/擦画行数增减无残影（用可断言的 fake console 捕获写序列）。
- **QuestionPrompt/PermissionPrompt 迁移回归**：业务结果不变（OtherChoice 续文本、Required/NotRequired、scope 过滤、Panel 概要仍在）。
- 复用 `FakeTerminalSurface`（+`PromptListAsync`）、新增 fake keys 源。

## 12. 实现分解（WBS，Wave 并行；文件所有权互斥）

**Batch 0（前置，1 Agent 或主控，串行）**：§5 全部新类型文件落地 + 空/最小实现，**编译通过**，冻结签名。含 `TuiInputTypes` 扩展、`TuiAnchorProbe`、`TuiHitRegion`、`TuiPromptContext`、`SelectableList`(签名)、`TuiListPrompt`(签名)、`ITerminalSurface.PromptListAsync`、`TuiPromptInputRelay.PromptReader`、`RawInputReader` 新构造签名。

**Wave 1（并行；各改独立文件；仅依赖 Batch 0）**
- W1-A「输入层」：`RawInputReader`(SGR/DSR 解析 + 上报开关 + 关闭)、`InputKeyDecoder`(Mouse)、`ChannelAnsiConsoleInput`(MapLocked 忽略 Mouse + relay 暴露 PromptReader) + 单测。
- W1-B「内核层」：`SelectableList` 完整实现、`TuiAnchorProbe` 完整实现、`TryHitTest` 纯函数、`TuiListPrompt` 自绘控件实现 + 单测（fake console/keys）。
- W1-C「surface/渲染接线」：`SpectreTerminalSurface`(PromptListAsync 构造 ctx、DSR 写点 PlaceCaret 前、MouseEnabled 门控)、`ITerminalSurface`、`FakeTerminalSurface`、`TuiActiveView`+`CompletionHits`+`TuiRenderer.BuildCompletions`(高亮/截断/命中表)、`TuiCliOptions.MouseEnabled`+`Parse`+`Program` DI 接线 + 渲染几何单测。

**Wave 2（并行；依赖 Wave 1）**
- W2-X「引擎-补全集成」（**独占 `TuiChatEngine.cs`**）：`_completionSelector`、`HandleInputAsync` 前置路由、`AcceptCompletion`、`TickRenderSnapshot` 选择态、anchor 消费/resize 失效、`PromptPendingAsync` 迁到新 surface、废弃 Tab 模态/`PromptChoiceAsync`/`CompletionCancelSentinel` + 路由单测。
- W2-Y「问答迁移」：`QuestionPrompt` 单/多选 → `TuiListPrompt`（保持对外签名）+ 回归测试。
- W2-Z「权限迁移」：`PermissionPrompt` → `TuiListPrompt`（保持对外签名）+ 回归测试。

**Wave 3（收尾，串行，关键节点）**：全量 `dotnet build` + `dotnet vstest …/Seeing.Agent.Tui.Tests.dll`；修失败；TUI README + `docs/architecture`（如涉及）增补「鼠标候选/降级/Shift 复制」说明；一次集成 code review。

**文件所有权矩阵**（防并行改同文件）：`TuiChatEngine.cs`→仅 W2-X；`ChannelAnsiConsoleInput.cs`→仅 W1-A；`SpectreTerminalSurface.cs`/`TuiRenderer.cs`/`Program.cs`/`TuiCliOptions*`→仅 W1-C；`RawInputReader/InputKeyDecoder`→仅 W1-A；`QuestionPrompt.cs`→仅 W2-Y；`PermissionPrompt.cs`→仅 W2-Z。

## 13. 验收清单
1. 输入 `/`→内联下拉出现、第一项高亮；继续打字过滤且游标复位；↑/↓移动高亮；Enter/Tab 把高亮命令带入输入框（其后空格、光标就绪）且**不提交**；再 Enter 才提交。
2. 补全下拉：划过行→高亮跟随；点击行＝带入。
3. 问答单选/权限：划过/点击生效；键盘上下/Enter/Esc 生效；`OtherChoice` 续文本正常；权限 Panel 概要 + 列表无残影。
4. 问答多选：点击切换 ☐/☑，Enter 提交勾选集；Required/NotRequired 不变。
5. 下拉可见时 Esc 仅收起下拉（输入不丢、不误触取消/清空/执行取消）；无下拉时 Esc 维持旧语义（含执行中二次确认，鼠标移动不冲掉）。
6. `--no-mouse`：无鼠标事件、终端鼠标交还用户（选中不需 Shift）、键盘体验完整。
7. 非 TTY/不支持终端：正常键盘运行，不崩、不乱码、不残留鼠标模式序列、DSR 不回时不误命中。
8. 全量单测通过（VSTest）。

## 14. 实现回填（v2.1，与代码对账 + 集成审查 M-1~M-4/m-2/m-3 处置）

实现落地后经集成 code review 修订，§5 冻结契约与实现的**已核对偏差**：

| 项 | §5 原契约 | 实现实际 | 说明 |
|----|-----------|----------|------|
| DSR 探测代次 | `long BeginProbe(Action)` 单入口 | **双签名**：`long BeginProbe(Action)`（模态控件自增）+ `void BeginProbe(long gen, Action)`（内联下拉由 engine 分配 frameGen 随帧传入） | 使底锚代次与命中表 `FrameGen` 同源；`NullTuiAnchorProbe` 两者皆 no-op 不写 `ESC[6n` |
| 在途配对 | 「发一个收一个」pending 覆盖 | **FIFO 队列**（`Queue<long>`，上限 16 丢最旧）：`Report` 弹队首代次配对回复 | 修 M-2：多查询在途时旧覆盖式会错配；FIFO + `TuiHitTest` 代次相等校验双重兜底，漂移即安全失效回落键盘 |
| `TuiPromptContext` | 含 `WriteHeader`/`RunListAsync` | 纯数据 `{Console, Keys(nullable), Anchor, MouseEnabled, Width}` | header（权限 Panel）由调用方 `ctx.Console.Write(...)+WriteLine()` 一次性画（修 m-2 换行贴边）；控件逻辑全在 `TuiListPrompt` |
| `ITerminalSurface` | 仅加 `PromptListAsync` | 另加 `UpdateAsync(view, caret, long probeGen, ct)` **重载**（旧三参保留、DIM） | 携带 engine 的 frameGen 到渲染线程 DSR 写点（`UpdateTarget` 后、`PlaceCaret` 前） |
| `TuiPromptInputRelay.PromptReader` | 暴露 | **移除**（未用）；surface 用 `ChannelAnsiConsoleInput.BoundReader` | 修 m-3 死成员 |

**集成审查必修项处置（均已实现 + 测试）**：
- **M-1 下拉可见性单一真源**：`RenderAsync` 候选改为 `_completionSelector is {Count:>0} ? GetCompletionCandidates() : []`——Esc 置空 selector 后本帧即隐藏，↑/↓ 不再误翻历史、二次 Esc 正常清空。
- **M-2** DSR FIFO（见上表）。
- **M-3 hover 同项不重绘**：`SelectableList.SetByHit` 仅当下标**实际改变**才 `true`→`ForceRender`。
- **M-4 空闲鼠标不整帧重写**：新增 `_inputVisualDirty` 标志，仅鼠标忽略/无状态变化时置 false，主循环据此走 tick 脏检查（`tickOnly = !_inputVisualDirty`），保「不擦输入法组合串」防护。
- **m-2 模态行留 1 列余量**（`usable = width-1`）防自动换行破坏擦画/DSR 基准。
- **m-1 多选 `preChecked`（DefaultSelectedLabels 映射）** 为有意增强（spec §7.2），非回归。
- Tab 模态选择器（`PromptChoiceAsync`/`CompletionCancelSentinel`）废弃；`/model` 等命令选择器 `TuiCommandRouter.PromptSelectionAsync` 保持现状（键盘）。

**已知边界**：`RenderAsync` 端到端 probeGen/resize 与 M-1/M-4 的引擎级集成用例因引擎 27 依赖未直测（以 `internal` 纯决策函数 + `TuiCompletionRouting`/`SelectableList`/`TuiHitTest`/`TuiAnchorProbe`/渲染器 hits 单测覆盖决策内核）；建议真机 spike 验证宽行/慢终端 hover 抖动（spec §12 Wave3 P3）。

