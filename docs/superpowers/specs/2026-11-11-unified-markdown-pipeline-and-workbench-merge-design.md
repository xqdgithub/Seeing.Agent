# 统一 Markdown 管线 + 合并 Markdown 预览工作台

**日期：** 2026-11-11
**状态：** 设计已确认，待实施
**触发问题：** 「monaco 文件管理是否自己就可以支持 md 渲染，这样就不用在单独开一个 md 工作台了」

---

## 1. 调研结论与真实问题

### 1.1 Monaco 不支持 Markdown 预览（已实测）

用本仓库实际打包的 Monaco（BlazorMonaco 3.5.0 → monaco-editor 0.55.1）在 Chromium 中实测：

| 检查项 | 结果 |
|---|---|
| `monaco` 顶层命名空间 | `CancellationTokenSource, Emitter, KeyCode, KeyMod, MarkerSeverity, MarkerTag, Position, Range, Selection, SelectionDirection, Token, Uri, createWebWorker, css, editor, html, json, languages, lsp, typescript` —— **无 `markdown`** |
| markdown 编辑器上的动作（过滤 `/preview\|markdown/i`） | 仅 `editor.action.showDefinitionPreviewHover`（跳转定义悬浮，与文档预览无关），共 126 个动作 |
| `monaco.editor.colorize(表格, 'markdown')` | `<span class="mtk6">\| a \| b \|</span><br/><span class="mtk6">\|---\|---\|</span>…` —— 逐 token 着色 span，**不产出 `<table>`** |
| `require(['vs/base/browser/markdownRenderer'])` | `Error: [object Event]` —— 内部模块不可加载 |
| `webview` | 仅出现在 URI scheme 枚举（`webviewPanel="webview-panel"`），**无 webview 宿主** |

`vs/…/markdown-C_rD0bIw.js` 全文件 3772 字节，内容是一份 Monarch tokenizer（正则 + token 类型表），只负责上色。

上游确认：VS Code 维护者 Matt Bierner 明确「This functionality is not part of Monaco」；VS Code 的 "Open Preview" 来自 `markdown-language-features` 扩展（markdown-it + webview），不在编辑器核心内。

**结论：Monaco 只能做语法高亮的源码编辑，永远替代不了渲染层。**

### 1.2 真实问题是管线漂移与能力重叠

现状有 **4 条独立的 Markdown 渲染管线**，其中 2 条不支持表格：

| # | 位置 | 管线 | 表格 | frontmatter |
|---|---|---|---|---|
| 1 | `WebUI/Rendering/Caching/MemoryRenderCache.cs`（会话消息） | 全量扩展 + 横向滚动 | ✅ | 保留 |
| 2 | `WebUI.Files/Workbenches/MarkdownRenderer.cs`（MD 工作台） | `UseAdvancedExtensions()` | ✅ | 剥离 |
| 3 | `WebUI.Files/Components/FileViewer.razor`（`.md` 只读兜底） | `new MarkdownPipelineBuilder().Build()` | ❌ | 保留 |
| 4 | `WebUI/Pages/MemoryDetailPage.razor`（记忆详情） | 同上，裸管线 | ❌ | 剥离（调用方做） |

实测 3/4 号管线渲染 `| a | b |` 的输出是 `<p>| a | b |<br/>|---|---|…`（竖线当普通字符）。

第 3 条尤其容易踩：文件工作台里可编辑的 `.md` 走 Monaco，但**截断 / 非 UTF-8 / 超大文件**走 `FileViewer`，那一刻表格就退化成一堆竖线。

同时 `MarkdownWorkbench`（151 行）与 `FilesWorkbench` 能力重叠：后者已能打开任意文件，只缺「渲染态」。

---

## 2. 目标

1. **收敛管线**：四处渲染共用一份管线定义，消除漂移。
2. **合并工作台**：一个「文件」工作台同时提供编辑与预览，删除独立的 Markdown 工作台。
3. **保持 agent 兼容**：`open_workbench(workbenchId=markdown, …)` 继续可用（走别名）。
4. **不丢上下文能力**：`Context.Report` 的「用户正在看哪个文档」语义必须保留。

## 3. 非目标

- 不做左右分屏同步滚动（窄侧栏 380px 放不下；文件工作台已有 diff 视图抢空间）。
- 不引入 markdown-it / 客户端渲染（.NET 侧 Markdig 已是既有选择）。
- 不改动会话消息的渲染策略（流式跳过 Markdown 等行为保持不变）。
- 不做与本次目标无关的重构。

---

## 4. 设计

### 4.1 新增原语包 `Seeing.Markdown`

磁盘位置 `src/primitives/Seeing.Markdown/`，依赖纪律与 `Seeing.Files` / `Seeing.Session` 同级：

```
Seeing.Markdown/
├── Seeing.Markdown.csproj      // 仅 Markdig + BCL，零 Seeing.* 依赖
├── MarkdownPipelineFactory.cs  // 两条管线的唯一构造点
├── MarkdownTableExtension.cs   // 由 WebUI/Rendering/Markdown/ScrollableTableExtension.cs 迁入
└── MarkdownFrontMatter.cs      // 由 WebUI.Files/Workbenches/MarkdownRenderer.StripFrontMatter 迁入
```

**为什么是原语层而不是支持层**：`Seeing.Agent.Tools.Support` 等服务支持包要求依赖 `Seeing.Agent.Abstractions`。本包只是 Markdig 的薄封装（构造管线 + 剥 frontmatter），不需要任何 Agent 契约，放原语层可以同时被 WebUI（宿主）、WebUI.Files（Razor 类库）、WebUI.Workbench 引用而不产生反向依赖。

**为什么不能放 WebUI 主项目**：`WebUI.Files` 是独立的 Razor 类库（供工作台与未来复用），不能反向引用宿主应用。

### 4.2 管线工厂：两个入口

```csharp
public static class MarkdownPipelineFactory
{
    /// 完整管线：全量扩展 + 表格横向滚动容器。所有场景共用同一实例。
    public static MarkdownPipeline Pipeline { get; }

    /// 渲染普通 Markdown 内容（会话消息）。frontmatter 原样保留——
    /// 消息里开头的 `---` 是分隔线，不是元数据。
    public static string Render(string? content);

    /// 渲染文档（文件预览 / 记忆详情 / 工作台）。先剥离 YAML frontmatter 再渲染——
    /// 否则闭合的 `---` 会被 Markdig 当成 setext 标题下划线，把整段元数据渲染成一个巨大标题。
    public static string RenderDocument(string? content);
}
```

管线内容 = 现 `MemoryRenderCache` 那份（`DisableHtml` + `UseAutoLinks`/`UseTaskLists`/`UsePipeTables`/`UseGridTables`/`UseListExtras`/`UseEmphasisExtras`/`UseAutoIdentifiers`/`UseCitations`/`UseCustomContainers`/`UseFooters`/`UseFootnotes`/`UseMathematics`/`UseSmartyPants` + `Use<MarkdownTableExtension>()`）。

**为什么以会话消息的管线为基准**：它是最全的一份（独有表格横向滚动扩展），且是用户日常看到最多的渲染路径。其余三份都是它的子集。

**`DisableHtml` 保留**：现有行为是禁止原始 HTML 透传，防注入。三处新接入点沿用同一约束，不放松。

### 4.3 四接入点改造

| 接入点 | 现状 | 改为 |
|---|---|---|
| `MemoryRenderCache` | 内联构造管线 | `MarkdownPipelineFactory.Render(content)` |
| `FileViewer.razor` | 裸管线 `Build()` | `MarkdownPipelineFactory.RenderDocument(f.Content)` |
| `MemoryDetailPage.razor` | 裸管线 + 调用方剥 frontmatter | `MarkdownPipelineFactory.RenderDocument(content)`（内部剥） |
| 文件工作台预览 | 无 | `MarkdownPipelineFactory.RenderDocument(content)` |

`MemoryRenderCache` 保留自己的 `ConcurrentDictionary` LRU 缓存层（缓存键、淘汰策略与渲染无关），只把 `Markdown.ToHtml(content, _markdownPipeline)` 换成工厂调用。

### 4.4 文件工作台的预览切换

**状态归属：`OpenFile.PreviewMode`（per-file）**

```csharp
public sealed class OpenFile
{
    // 现有字段…
    /// <summary>是否处于预览态（仅 Markdown 有意义；默认预览）。</summary>
    public bool PreviewMode { get; set; }
}
```

初值在 `FileTabsState.Open` 新建标签时设置：`PreviewMode = file.Kind == FileContentKind.Markdown`。

**为什么 per-file 而不是全局**：同一工作台里 `README.md` 看渲染、`notes.md` 改源码是正常需求。状态跟标签走，切走再切回不丢。

**切换入口：`FileTabsState.TogglePreview(FileResourceId id)`**

```csharp
public void TogglePreview(FileResourceId id)
{
    if (!_byKey.TryGetValue(Key(id), out var of)) return;
    if (of.Kind != FileContentKind.Markdown || of.IsDeleted) return;
    of.PreviewMode = !of.PreviewMode;
    Changed?.Invoke();
}
```

**按钮位置：`FileTabs.razor` 标签栏右侧，与现有保存按钮并排**

只在 `Active.Kind == Markdown && !Active.IsDeleted` 时显示；文案随状态在「预览」/「编辑」间切换（点它进入另一个模式）。

**渲染分支：`FileEditor.razor`**

```razor
else if (f.Kind is FileContentKind.Markdown && f.PreviewMode)
{
    <FileViewer Tabs="Tabs" />
}
else
{
    <div class="file-editor-monaco"> …StandaloneCodeEditor… </div>
}
```

分支插在现有 `PendingDiff` 判断**之后**、Monaco 之前 —— diff 优先级最高，不受预览开关影响。

**为什么复用 `FileViewer` 而不是新写预览组件**：`FileViewer` 已实现 Markdown / 图片 / 二进制 / 只读文本四种分支，`Markdown` 分支只是缺一份正确管线（见 4.3）。复用它意味着预览态同时覆盖「`.md` 主动预览」与「超大文件被动只读」两种来源，行为天然一致。

### 4.5 `markdown` 别名路由

**删除** `FileWorkspaceModule.DescribeUi()` 中的 markdown 声明，**在宿主侧做别名解析**。

`WorkbenchRequestKinds` 新增：

```csharp
/// <summary>已合并的历史工作台 id：等价于 files + openPayload.preview=true。</summary>
public const string LegacyMarkdownId = "markdown";
```

`WorkbenchSurface.Open(request)` 在 `workbenchId` 解析后、查找声明前插入：

```csharp
if (string.Equals(workbenchId, WorkbenchRequestKinds.LegacyMarkdownId, StringComparison.Ordinal))
{
    workbenchId = "files";
    openPayload = MergePreviewFlag(openPayload);   // 保证 preview = true
}
```

**为什么放宿主而不是工具侧**：`OpenWorkbenchTool` 的五道门闸（扩展点 / 未知 id / 模块未启用 / 载荷校验 / 可呈现界面）是对声明表的通用查询。把别名塞进工具会让每道门闸各多一个特例分支，且工具拿不到「宿主怎么解释这个 id」的知识。别名是渲染层的语义，归 `WorkbenchSurface`。

**为什么保留别名而不是直接报错**：模型已形成的调用习惯、历史对话里的工具调用记录、以及 `docs/architecture/41` 里的示例都是 `workbenchId=markdown`。别名让这些继续工作，成本只是一个 `if`。

### 4.6 `Context.Report` 补齐

`FilesWorkbench` 当前从不调 `Context.Report`，直接删掉 MD 工作台会让 agent 失去「用户正在看哪个文档」这个上下文。

在 `FilesWorkbench` 增加：

- 激活标签变化时上报
  ```csharp
  Context.Report(new WorkbenchViewReport
  {
      ViewState = new() { ["path"] = relativePath, ["preview"] = preview ? "1" : "0" },
      ContextSummary = preview ? $"正在预览 {relativePath}" : $"正在编辑 {relativePath}"
  });
  ```
- 初始化时读 `ViewState["path"]` 重开该文件（对应 MD 工作台原有的「刷新后恢复上次查看的文件」）。

上报触发点：`PanelState.Changed`（已有订阅）与标签激活变化（`FilePanelHost.OnTabsChanged` 已有钩子），不新增定时器。

### 4.7 agent 可见的变化

| 项 | 变化 |
|---|---|
| `list_workbenches` | 从 `files, markdown` 变为只有 `files`（`markdown` 不再作为独立工作台列出） |
| `open_workbench(workbenchId=files, payload={path, preview?})` | 新增可选 `preview` 布尔键（默认 `true`，仅对 `.md` 生效） |
| `open_workbench(workbenchId=markdown, payload={path})` | 仍然成功，落到 files 并置 `preview=true` |

`files` 工作台的 `OpenPayloadSchema` 增加：

```json
"preview": { "type": "boolean", "description": "是否以预览态打开（仅 Markdown 文件有效，默认 true）" }
```

`required` 仍为 `["path"]`，`preview` 可选。

---

## 5. 删除清单

| 文件 / 片段 | 动作 |
|---|---|
| `samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownWorkbench.razor`（151 行） | 删 |
| `samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownRenderer.cs`（63 行） | 删（能力迁入 `Seeing.Markdown`） |
| `samples/Seeing.Agent.WebUI/Shell/WebUiWorkbenchViews.cs`：`Register(..., "markdown", typeof(MarkdownWorkbench))` | 删 |
| `src/capabilities/Seeing.Agent.Files/FileWorkspaceModule.cs`：markdown 的 `WorkbenchContribution` 块 + `DescribeUi` 注释调整 | 删 |
| `samples/Seeing.Agent.WebUI/wwwroot/css/workbench.css`：`.md-workbench` 及全部 `.md-workbench-*` 规则（第 86–190 行，含 `.md-workbench-empty/loading/error/title/text/retry`） | 删 |
| `samples/Seeing.Agent.WebUI/Components/WorkbenchPicker.razor`：`"markdown" => "📝"` | 删 |
| `samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchTabStrip.razor`：`"markdown" => "📝"` | 删 |
| `samples/Seeing.Agent.WebUI/Rendering/Markdown/ScrollableTableExtension.cs` | 移到 `Seeing.Markdown`（命名空间改 `Seeing.Markdown`） |
| `docs/architecture/41-workbench-plugin-platform-release-notes.md`：工作台清单、失败文案示例、验收项 | 更新 |
| `docs/architecture/README.md` / `AGENTS.md` WHERE TO LOOK：Markdown 预览工作台行 | 更新 |

**保留不动**：`.message-content`（会话消息）、`.file-markdown`（FileViewer）、`markdown-body`（记忆详情）三套容器样式——它们服务的是不同版式，不该因管线合并而统一。

---

## 6. 测试

### 6.1 新建 `tests/primitives/Seeing.Markdown.Tests`

| 用例 | 断言 |
|---|---|
| `Render_DocumentWithFrontMatter_ShouldKeepSeparator` | `---\ntitle: x\n---\n正文` 渲染后含 `<hr>`（分隔线保留，标题不被吞） |
| `RenderDocument_DocumentWithFrontMatter_ShouldStripMetadata` | 同上输入，输出不含 `title: x`，且不含被放大成 `<h2>` 的整段 |
| `Render_Table_ShouldWrapInScrollContainer` | 含 `<div class="md-table-scroll">` 且只出现一次 |
| `Render_Table_ShouldProduceTableElement` | 含 `<table>`（回归 3/4 号管线的缺陷） |
| `Render_RawHtml_ShouldBeEscaped` | `<script>` 不出现在输出中（`DisableHtml` 契约保持） |
| `Render_NullOrEmpty_ShouldReturnEmpty` | 空输入返回 `string.Empty`，不抛 |

### 6.2 `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTabsStateTests`

| 用例 | 断言 |
|---|---|
| `Open_MarkdownFile_ShouldDefaultToPreview` | 新建 `Kind=Markdown` 标签的 `PreviewMode == true` |
| `Open_TextFile_ShouldNotUsePreview` | `Kind=Text` 的 `PreviewMode == false` |
| `TogglePreview_MarkdownFile_ShouldFlip` | 连点两次回到原值，每次触发一次 `Changed` |
| `TogglePreview_NonMarkdown_ShouldBeNoOp` | `Kind=Text` 调 toggle 不变、不触发 `Changed` |

### 6.3 现有测试同步

- `tests/capabilities/Seeing.Agent.Workbench.Tests`：所有把 `markdown` 当独立工作台的断言（`WorkbenchSessionStateStoreTests`、`WorkbenchContextSourceTests`、`WorkbenchRequestKindsTests`、`WorkbenchToolsTests`）改用其他 id 或改为别名语义断言。
- `MarkdownTableScrollTests`（WebUI.Tests）：`ScrollableTableExtension` 迁走后改用工厂入口断言，CSS 契约部分保留。
- `MessageContentClassContractTests`：不受影响（断言的是 Razor 容器的类名）。

### 6.4 新增别名路由测试

`WorkbenchSurface` 是 Razor 组件，别名逻辑抽成可测的静态方法：

```csharp
internal static (string WorkbenchId, JsonElement? OpenPayload) ApplyLegacyAlias(
    string workbenchId, JsonElement? openPayload);
```

用例：
- `markdown` + `{path}` → `files` + `{path, preview:true}`
- `markdown` + `{path, preview:false}` → `files` + `{path, preview:false}`（显式值不被覆盖）
- `files` + 任意 → 原样透传
- `markdown` + 无 payload → `files` + `{preview:true}`

---

## 7. 风险与取舍

| 风险 | 缓解 |
|---|---|
| `Seeing.Markdown` 是新项目，solution 与 CPM 需同步 | 加入 `Seeing.Agent.slnx`、`Directory.Packages.props` 已有 Markdig 1.4.0（`Seeing.Markdown` 用 CPM 版本；WebUI/WebUI.Files 的 `Version="1.3.2"` 字面量保持不动，避免顺带升级） |
| 删 MD 工作台后旧会话 `ViewState` 里有 `workbench:markdown` 快照 | `WorkbenchSessionStateStore` 按 id 存快照，读不到声明时丢弃即可（现有 `WorkbenchProjection` 走可见集）；需在实现时验证不会因未知 id 抛异常 |
| 预览态下用户按 Ctrl+S | 预览不是编辑器，保存按钮仍可用（保存的是磁盘现有内容，无脏值）。`HasActiveEditable` 判断不变 |
| 别名让 `list_workbenches` 与 `open_workbench` 不一致（列出没有 markdown，但传 markdown 能成功） | 接受。`list_workbenches` 是「你能开什么」，别名是「旧名字还能用」。工具失败文案里附的可用 id 列表已足够引导模型改用 `files` |
| 4 条管线合一后，某处视觉回归 | 6.1 的表格/HTML 转义/空输入用例覆盖共性行为；四接入点各自的容器 CSS 不动（`.message-content` / `.file-markdown` / `markdown-body` / `.md-workbench-body` 中前者保留、后两者保留） |

---

## 8. 实施顺序

1. 建 `Seeing.Markdown` 原语包 + 迁入表格扩展与 frontmatter 剥离 + 单测（此时无人引用，可独立验证）。
2. 四接入点切到工厂，删各自的管线条目；跑全量测试确认无视觉/行为回归。
3. `OpenFile.PreviewMode` + `TogglePreview` + `FileTabs` 按钮 + `FileEditor` 分支 + 单测。
4. `files` 声明增加 `preview` 键；`WorkbenchSurface` 别名路由 + 单测。
5. `FilesWorkbench` 补 `Context.Report` + `ViewState` 恢复。
6. 删除 MD 工作台组件、CSS、视图登记、declaration；同步测试与文档。
7. 跑 `dotnet build` + 全部测试项目；浏览器手验（预览/编辑切换、表格滚动、别名打开）。

---

## 9. 验收标准

- [ ] `dotnet build` 零错误。
- [ ] 全部测试项目通过（含新建 `Seeing.Markdown.Tests`）。
- [ ] `grep -r "MarkdownPipelineBuilder"` 在 `samples/` 与 `src/` 下只剩 `Seeing.Markdown` 内部一处。
- [ ] `list_workbenches` 只列 `files`。
- [ ] `open_workbench(workbenchId=markdown, payload={path:"README.md"})` 打开文件工作台且处于预览态。
- [ ] 文件工作台打开 `.md` 默认预览；点「编辑」切 Monaco；再点「预览」切回。
- [ ] 预览态下多列表格可左右滚动（此前被 `overflow-x:hidden` 裁掉）。
- [ ] `FileViewer` 打开超大/截断 `.md` 时表格正常渲染（回归 3 号管线缺陷）。
- [ ] 记忆详情页表格正常渲染（回归 4 号管线缺陷）。
- [ ] agent 上下文里能看到「正在预览 X」。
