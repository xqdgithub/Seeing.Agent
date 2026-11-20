# 系统提示词分节边界统一 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让每个进入系统提示词的注入块都带明确 H2 分节边界，并在装配层对缺失边界的 System 源告警。

**Architecture:** 只改三处渲染文本（协同目录、工具目录）与一处装配校验（`ContextAssembler`），不新增抽象、不调整既有分节顺序。守门只记录非致命 `Issues` 与 warning，不自动补标题、不抛异常。

**Tech Stack:** .NET 10、C# 12、xUnit + FluentAssertions、Microsoft.Extensions.Logging。

**Spec:** `docs/superpowers/specs/2026-11-20-prompt-section-boundaries-design.md`

## Global Constraints

- 分节形态统一为 Markdown H2（`## `），不引入 XML 标签包裹。
- 不调整既有分节相对顺序，不重命名 `## Skills` / `## Agents` / `## Environment`。
- 只改 `ContextSourceTarget.System` 的源；`Message` 落点（含 `ProjectInstructionsContextSource` 的 XML 信封）不动。
- 不重写已落库基线：新文本只对新建会话或重锚定（压缩 / 切换工作区）生效。
- 渲染必须字节稳定：只依赖 canonical 字段，排序用 ordinal，换行显式 `'\n'`。
- 测试禁止固定时长等待；断言用条件轮询或不变量。
- 远程操作走 glab，本地只允许 commit，不 push。

## Review Focus

1. System 源渲染为空白文本时不得产生「缺少分节」告警（空基线是合法的「无内容」）。
2. 以 XML 信封开头的 System 源（`<xxx>…</xxx>`）必须通过守门，不能被误报。
3. 工具目录预算刚好顶满时，加标题后 `CatalogText.Length` 仍不得超过 `CatalogBudgetChars`。
4. 协同目录备注含换行/多余空白时，不得渲染出第二个伪分节。
5. 协同目录路径含中文、空格、尾分隔符时，列表仍是一行一条、不破坏列表结构。

---

### Task 1: 协同目录分节与文案

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Context/CollaborationDirectoriesContextSource.cs:40-45,113-135`
- Test: `tests/spine/Seeing.Agent.Tests/Context/CollaborationDirectoriesContextSourceTests.cs`

**Interfaces:**
- Consumes: 无（该类既有 `ICollaborationDirectories` / `IContextExecutionScope` 构造）
- Produces: 常量 `Heading = "## 协同工作目录"`、`Guidance`、`EmptyText`（`private const`，仅本类使用）

- [ ] **Step 1: 改测试期望（先失败）**

在 `CollaborationDirectoriesContextSourceTests.cs` 中：

`ReadAsync_EmptyList_ShouldReturnValueAndRenderNone` 的渲染断言改为：

```csharp
var text = source.RenderInitial(value);
text.Should().StartWith("## 协同工作目录");
text.Should().Contain("未授权任何协同目录");
text.Should().NotContain("（无）");
```

`RenderInitial_ShouldNotRenderWorkingDirectory` 追加：

```csharp
text.Should().StartWith("## 协同工作目录");
text.Should().Contain("绝对路径");
text.Should().Contain("workdir");
text.Should().NotContain("以 Environment 分节的工作目录为基准");
```

新增用例：

```csharp
[Fact]
public async Task RenderInitial_WithDirectory_ShouldListOneEntryPerLine()
{
    var directory = CreateDirectory();
    var spaced = Path.Combine(directory, "报表 输出");
    Directory.CreateDirectory(spaced);

    var directories = new FakeDirectories();
    directories.Entries.Add(new CollaborationDirectory(directory, "报表\n输出"));
    directories.Entries.Add(new CollaborationDirectory(spaced));
    var source = CreateSource(directories);

    var text = source.RenderInitial(await source.ReadAsync(TestContext.Current.CancellationToken));

    text.Should().StartWith("## 协同工作目录");
    text.Should().Contain($"\n- {directory}（报表 输出）");
    text.Should().Contain($"\n- {spaced}");
    text.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)).Should().Be(2);
}
```

`Render_AddedAndRemoved_ShouldNarrateBoth` 末行改为：

```csharp
removedText.Should().NotContain("## 协同工作目录");
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~CollaborationDirectoriesContextSourceTests"
```

Expected: FAIL（首行仍是「协同工作目录（可读写…）:」，且仍含「（无）」）

- [ ] **Step 3: 实现文案**

把 `Title` / `EmptyLine` 替换为：

```csharp
private const string Heading = "## 协同工作目录";

private const string EmptyText = "本会话未授权任何协同目录；可访问范围只有工作目录。";

private const string Guidance =
    "这些是本会话额外授权的可读写目录，与 `## Environment` 中的工作目录平级，不是它的子目录。\n" +
    "访问时使用下面列出的绝对路径，或在命令中用 workdir 参数指定；相对路径只相对工作目录解析，不相对这些目录解析。";
```

`RenderInitial` 改为：

```csharp
var entries = EntriesFrom(value);

if (entries.Count == 0)
    return Truncate($"{Heading}\n\n{EmptyText}");

var builder = new StringBuilder();
builder.Append(Heading).Append('\n').Append('\n').Append(Guidance).Append('\n').Append('\n');

foreach (var entry in entries)
{
    builder.Append("- ").Append(entry.Path);
    if (!string.IsNullOrEmpty(entry.Note))
        builder.Append('（').Append(entry.Note).Append('）');
    builder.Append('\n');
}

return Truncate(builder.ToString().TrimEnd('\n'));
```

- [ ] **Step 4: 运行测试确认通过**

同 Step 2 命令，Expected: PASS（全部用例）。

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Context/CollaborationDirectoriesContextSource.cs tests/spine/Seeing.Agent.Tests/Context/CollaborationDirectoriesContextSourceTests.cs
git commit -m "feat(prompt): give collaboration directories an H2 section"
```

---

### Task 2: 工具目录 H2 标题

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs:300-355`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs`

**Interfaces:**
- Consumes: `ToolExposureEngine.Compute` 既有签名与 `CatalogText`
- Produces: `CatalogText` 以 `## 工具清单` 开头（分层布局且存在延迟工具时）

- [ ] **Step 1: 写失败测试**

在 `ToolExposureEngineTests.cs` 新增：

```csharp
[Fact]
public void TieredCatalog_ShouldStartWithToolCatalogHeading()
{
    var tools = new[] { Schema("a"), Schema("b"), Schema("tool_search") };

    var result = ToolExposureEngine.Compute(
        Input(tools, Config(ToolExposurePolicyMode.Tiered)), Counter);

    result.CatalogText.Should().NotBeNull();
    result.CatalogText!.Should().StartWith("## 工具清单");
    result.CatalogText.Should().Contain("### 通用");
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet build tests/hosting/Seeing.Agent.Hosting.Tests
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolExposureEngineTests"
```

Expected: FAIL（`CatalogText` 以「以下工具当前未内联…」开头）

- [ ] **Step 3: 加标题并修正预算**

在 `BuildCatalog` 中新增常量：

```csharp
private const string CatalogHeading = "## 工具清单";
```

预算计算改为（标题行 + 空行也计入）：

```csharp
var used = CatalogHeading.Length + 2 + intro.Length + headers.Sum(h => h.Length + 1);
```

渲染改为：

```csharp
var builder = new StringBuilder();
builder.Append(CatalogHeading).AppendLine().AppendLine();
builder.Append(intro).AppendLine().AppendLine();
```

其余分组渲染不变。

- [ ] **Step 4: 运行确认通过**

同 Step 2 命令，Expected: PASS，包括 `DefaultCatalogBudget_ShouldShowEveryDeferredTool` 的长度断言。

- [ ] **Step 5: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs
git commit -m "feat(prompt): give the tool catalog an H2 section"
```

---

### Task 3: 装配层分节守门

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Context/ContextAssembler.cs:546,582,671`（三个 `WarnIfBaselineTooLong` 调用点旁）与新增私有方法
- Test: `tests/spine/Seeing.Agent.Tests/Context/ContextAssemblerTests.cs`

**Interfaces:**
- Consumes: `IContextSource.Target`、`ContextSourceTarget.System`、既有 `issues` 列表
- Produces: `private void WarnIfSystemBaselineNotSectioned(string sessionId, IContextSource source, string baselineText, List<string> issues)`

- [ ] **Step 1: 写失败测试**

在 `ContextAssemblerTests.cs` 新增：

```csharp
[Fact]
public async Task SystemSourceWithoutHeading_ShouldReportIssue()
{
    var source = new FakeSource { RenderInitialBehavior = _ => "bare body" };
    var session = new SessionData { Id = "s-unsectioned" };
    var assembler = CreateAssembler(session, source);

    var result = await AssembleAsync(assembler, session);

    result.Issues.Should().Contain(issue => issue.Contains("分节", StringComparison.Ordinal));
    result.SystemBaseline.Should().Contain("bare body");
}

[Fact]
public async Task SystemSourceWithHeading_ShouldNotReportSectionIssue()
{
    var source = new FakeSource();
    var session = new SessionData { Id = "s-sectioned" };
    var assembler = CreateAssembler(session, source);

    var result = await AssembleAsync(assembler, session);

    result.Issues.Should().NotContain(issue => issue.Contains("分节", StringComparison.Ordinal));
}

[Fact]
public async Task EmptySystemSource_ShouldNotReportSectionIssue()
{
    var source = new FakeSource { RenderInitialBehavior = _ => string.Empty };
    var session = new SessionData { Id = "s-empty" };
    var assembler = CreateAssembler(session, source);

    var result = await AssembleAsync(assembler, session);

    result.Issues.Should().NotContain(issue => issue.Contains("分节", StringComparison.Ordinal));
}

[Fact]
public async Task XmlEnvelopeSystemSource_ShouldNotReportSectionIssue()
{
    var source = new FakeSource { RenderInitialBehavior = _ => "<note>\nbody\n</note>" };
    var session = new SessionData { Id = "s-xml" };
    var assembler = CreateAssembler(session, source);

    var result = await AssembleAsync(assembler, session);

    result.Issues.Should().NotContain(issue => issue.Contains("分节", StringComparison.Ordinal));
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ContextAssemblerTests"
```

Expected: FAIL（`Issues` 中没有分节告警）

- [ ] **Step 3: 实现守门**

新增私有方法：

```csharp
private void WarnIfSystemBaselineNotSectioned(
    string sessionId,
    IContextSource source,
    string baselineText,
    List<string> issues)
{
    if (source.Target != ContextSourceTarget.System || string.IsNullOrWhiteSpace(baselineText))
        return;

    var firstLine = baselineText.Replace("\r\n", "\n").Split('\n')[0].TrimStart();
    if (firstLine.StartsWith("## ", StringComparison.Ordinal))
        return;

    var trimmed = baselineText.Trim();
    if (trimmed.StartsWith('<') && trimmed.Contains("</", StringComparison.Ordinal))
        return;

    issues.Add(
        $"源 {source.Key.Value} 的系统基线缺少分节标题（应以 `## 标题` 开头或以 XML 标签包裹），可能与上一分节粘连。");
    _logger.LogWarning(
        "上下文源系统基线缺少分节边界：{SourceKey} {SessionId}",
        source.Key.Value,
        sessionId);
}
```

在 `EstablishBaseline`、`RebuildBaseline`、`TryReanchorBaseline` 的 `WarnIfBaselineTooLong(...)` 之后各加一行：

```csharp
WarnIfSystemBaselineNotSectioned(session.Id, source, baselineText, issues);
```

- [ ] **Step 4: 运行确认通过**

同 Step 2 命令，Expected: PASS（含既有的 `ContextAssemblerTests` 全部用例）。

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Context/ContextAssembler.cs tests/spine/Seeing.Agent.Tests/Context/ContextAssemblerTests.cs
git commit -m "feat(prompt): warn when a system context baseline has no section heading"
```

---

### Task 4: 架构文档与全量回归

**Files:**
- Modify: `docs/architecture/21-context-source-pipeline-release-notes.md`
- Test: 全量相关测试项目

**Interfaces:**
- Consumes: Task 1–3 的行为
- Produces: 文档中的分节约定；无代码接口

- [ ] **Step 1: 补文档**

在 `21-context-source-pipeline-release-notes.md` 末尾新增小节，内容至少包含：

```markdown
## 系统落点分节约定

- 每个 `ContextSourceTarget.System` 源的初始渲染必须以 `## 标题` 开头，或以 XML 信封包裹整体内容。
- 装配器在建立 / 重建 / 重锚定基线时校验该约定；不满足时记入 `Issues` 并输出 warning，不自动补标题、不抛异常。
- 新文本只对新建会话或重锚定生效；已落库基线按字节稳定原则不重写。
- 现有分节顺序：`## Skills` → `## 协同工作目录` → `## 工具清单`（System 源按 Scope → Key 排序）。
```

- [ ] **Step 2: 跑全量相关测试**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet build tests/hosting/Seeing.Agent.Hosting.Tests
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
```

Expected: 两次 vstest 全部通过，无失败用例。

- [ ] **Step 3: 提交**

```bash
git add docs/architecture/21-context-source-pipeline-release-notes.md
git commit -m "docs: record system prompt section boundary convention"
```
