# 统一 Markdown 管线 + 合并 Markdown 预览工作台 —— 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把仓库里 4 条 Markdown 渲染管线收敛到一个原语包，并把独立的「Markdown 预览」工作台合并进「文件」工作台（保留 `workbenchId=markdown` 别名）。

**Architecture:** 新建原语层 `Seeing.Markdown`（仅依赖 Markdig），暴露 `Render`（会话消息，保留 frontmatter）与 `RenderDocument`（文档，剥 frontmatter）两个入口。四处调用点全部改用它。文件工作台的 `OpenFile` 增加 per-file `PreviewMode`，标签栏加切换按钮，`FileEditor` 在 diff 之后、Monaco 之前插入预览分支并复用既有 `FileViewer`。宿主 `WorkbenchSurface` 把 `workbenchId=markdown` 别名解析为 `files + preview=true`。

**Tech Stack:** .NET 10、C# 13、Markdig 1.4.0（CPM）、Blazor Server、xUnit v3 + FluentAssertions、BlazorMonaco 3.5.0

## Global Constraints

- 目标框架一律 `net10.0`；新项目必须同时登记进 `Seeing.Agent.slnx` 与 `Directory.Packages.props`。
- `Seeing.Markdown` 是**原语层**：只允许引用 Markdig 与 BCL，**禁止**引用任何 `Seeing.Agent.*` / `Seeing.*` 项目。
- 包版本用中央包管理（`Directory.Packages.props`），新项目一律 `<PackageReference Include="Markdig" />`（不写字面量版本）。
- `samples/Seeing.Agent.WebUI*` 与对应测试项目 `ManagePackageVersionsCentrally=false`，版本写字面量（Markdig 保持现有 `1.3.2`，**不要**顺手升到 1.4.0）。
- 测试用 VSTest 直跑已构建程序集（MTP 在本环境发现不到测试）：
  `dotnet vstest <bin>/Debug/net10.0/<Assembly>.dll --TestCaseFilter:"FullyQualifiedName~ClassName"`
- **禁止**在测试里用 `Task.Delay` / `Thread.Sleep` 等固定等待（见 AGENTS.md「测试禁止等待型断言」）。
- **禁止**自建 `temp + Move` 原子写；需要原子写走 `Seeing.Files.AtomicFileWrite`。
- 路径边界判定一律调 `PathBoundary`，不得内联前缀比较。
- 每个 Task 结束必须 commit；commit message 用中文，格式 `<type>(<scope>): <摘要>` + 空行 + 正文。
- 删除代码时同步删除对应测试与文档引用，**不允许**留下无人引用的死代码。

---

## 文件结构

### 新建

| 路径 | 职责 |
|---|---|
| `src/primitives/Seeing.Markdown/Seeing.Markdown.csproj` | 原语包定义：仅 Markdig + BCL |
| `src/primitives/Seeing.Markdown/MarkdownPipelineFactory.cs` | 管线唯一构造点 + `Render` / `RenderDocument` 两个入口 |
| `src/primitives/Seeing.Markdown/MarkdownTableExtension.cs` | Markdig 扩展：表格包横向滚动容器（由 WebUI 迁入） |
| `src/primitives/Seeing.Markdown/MarkdownFrontMatter.cs` | YAML frontmatter 剥离（由 WebUI.Files 迁入） |
| `tests/primitives/Seeing.Markdown.Tests/Seeing.Markdown.Tests.csproj` | 原语包测试 |
| `tests/primitives/Seeing.Markdown.Tests/MarkdownPipelineFactoryTests.cs` | `Render` / `RenderDocument` 差异、表格、HTML 转义、空输入 |
| `samples/Seeing.Agent.WebUI.Workbench/Services/LegacyWorkbenchAlias.cs` | `markdown → files + preview` 别名解析（纯函数，可测） |
| `tests/apps/Seeing.Agent.WebUI.Workbench.Tests/Services/LegacyWorkbenchAliasTests.cs` | 别名路由用例 |

### 修改

| 路径 | 改动 |
|---|---|
| `Seeing.Agent.slnx` | 登记新项目（2 处：`/src/primitives/` 与 `/tests/primitives/`） |
| `Directory.Packages.props` | Markdig 已存在（1.4.0），无需改动 |
| `samples/Seeing.Agent.WebUI/Rendering/Caching/MemoryRenderCache.cs` | 删内联管线，改用 `MarkdownPipelineFactory.Render` |
| `samples/Seeing.Agent.WebUI.Files/Components/FileViewer.razor` | 删裸管线，改用 `MarkdownPipelineFactory.RenderDocument` |
| `samples/Seeing.Agent.WebUI/Pages/MemoryDetailPage.razor` | 删裸管线 + 手动剥 frontmatter，改用 `RenderDocument` |
| `samples/Seeing.Agent.WebUI.Files/Seeing.Agent.WebUI.Files.csproj` | 增加 `Seeing.Markdown` 项目引用 |
| `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj` | 增加 `Seeing.Markdown` 项目引用 |
| `samples/Seeing.Agent.WebUI.Files/Services/FileTabsState.cs` | `OpenFile.PreviewMode` + `TogglePreview` |
| `samples/Seeing.Agent.WebUI.Files/Components/FileTabs.razor` | 预览/编辑切换按钮 |
| `samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor` | 预览分支（diff 之后、Monaco 之前） |
| `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor` | `OpenPathAsync` 支持 `preview` 参数 |
| `samples/Seeing.Agent.WebUI.Files/Workbenches/FilesWorkbench.razor` | 读 `preview` 载荷、补 `Context.Report`、`ViewState` 恢复 |
| `src/capabilities/Seeing.Agent.Files/FileWorkspaceModule.cs` | 删 markdown 声明；`files` 载荷增加 `preview` 键 |
| `src/abstractions/Seeing.Agent.Abstractions/Ui/Workbench/WorkbenchRequestKinds.cs` | 新增 `LegacyMarkdownId` 常量 |
| `samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchSurface.razor` | `Open()` 走别名解析 |
| `samples/Seeing.Agent.WebUI/Shell/WebUiWorkbenchViews.cs` | 删 markdown 视图登记 |
| `samples/Seeing.Agent.WebUI/Components/WorkbenchPicker.razor` | 删 `"markdown" => "📝"` |
| `samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchTabStrip.razor` | 删 `"markdown" => "📝"` |
| `samples/Seeing.Agent.WebUI/wwwroot/css/workbench.css` | 删 `.md-workbench*` 全部规则（第 86–190 行） |
| `tests/apps/Seeing.Agent.WebUI.Tests/Rendering/MarkdownTableScrollTests.cs` | 改用工厂入口（扩展迁走后不再引用 WebUI 命名空间） |
| `tests/capabilities/Seeing.Agent.Workbench.Tests/*` | 把 `markdown` 当独立工作台的断言改为别名语义 |
| `docs/architecture/41-workbench-plugin-platform-release-notes.md` | 更新工作台清单与示例 |

### 删除

| 路径 | 说明 |
|---|---|
| `samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownWorkbench.razor` | 151 行，能力并入 FilesWorkbench + FileViewer |
| `samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownRenderer.cs` | 63 行，能力迁入 `Seeing.Markdown` |
| `samples/Seeing.Agent.WebUI/Rendering/Markdown/ScrollableTableExtension.cs` | 迁入原语包 |

---

## Task 1：建立 `Seeing.Markdown` 原语包

**Files:**
- Create: `src/primitives/Seeing.Markdown/Seeing.Markdown.csproj`
- Create: `src/primitives/Seeing.Markdown/MarkdownFrontMatter.cs`
- Create: `tests/primitives/Seeing.Markdown.Tests/Seeing.Markdown.Tests.csproj`
- Create: `tests/primitives/Seeing.Markdown.Tests/FrontMatterTests.cs`
- Modify: `Seeing.Agent.slnx`

**Interfaces:**
- Produces: `Seeing.Markdown.MarkdownFrontMatter.Strip(string? content) → string?`
  - frontmatter 闭合时返回头之后的正文字符串（可能为空串）
  - 无 frontmatter（首行不是 `---`、或无闭合分隔线）时**原样返回入参**
  - `null` / 空串原样返回

- [ ] **Step 1：写失败的测试**

`tests/primitives/Seeing.Markdown.Tests/FrontMatterTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Markdown;

namespace Seeing.Markdown.Tests;

/// <summary>
/// frontmatter 剥离契约：文档类 Markdown 的 <c>---</c> 元数据头必须被剥掉，
/// 否则 Markdig 会把闭合的 <c>---</c> 当成 setext 标题下划线，
/// 把整段元数据渲染成一个巨大的标题（用户看到 id/type/tags 满屏）。
/// </summary>
public class FrontMatterTests
{
    [Fact]
    public void Strip_WithClosedFrontMatter_ShouldReturnBody()
    {
        var input = "---\ntitle: 记忆\nid: abc\n---\n正文第一段";

        MarkdownFrontMatter.Strip(input).Should().Be("正文第一段");
    }

    [Fact]
    public void Strip_WithDotsTerminator_ShouldReturnBody()
    {
        var input = "---\ntitle: x\n...\n正文";

        MarkdownFrontMatter.Strip(input).Should().Be("正文");
    }

    [Fact]
    public void Strip_WithoutFrontMatter_ShouldReturnInputVerbatim()
    {
        var input = "# 标题\n\n正文\n\n---\n\n分节";

        MarkdownFrontMatter.Strip(input).Should().Be(input,
            "首行不是 --- 时不得吞掉正文里的分隔线");
    }

    [Fact]
    public void Strip_WithUnclosedFrontMatter_ShouldReturnInputVerbatim()
    {
        var input = "---\ntitle: x\n正文没闭合";

        MarkdownFrontMatter.Strip(input).Should().Be(input,
            "没有闭合分隔线就不是 frontmatter，按原文渲染");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Strip_WithNullOrEmpty_ShouldReturnSame(string? input)
    {
        MarkdownFrontMatter.Strip(input).Should().Be(input);
    }

    [Fact]
    public void Strip_WithCrLfFrontMatter_ShouldHandleWindowsLineEndings()
    {
        var input = "---\r\ntitle: x\r\n---\r\n正文";

        MarkdownFrontMatter.Strip(input).Should().Be("正文");
    }
}
```

- [ ] **Step 2：建测试项目与包项目骨架**

`src/primitives/Seeing.Markdown/Seeing.Markdown.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Markdown</RootNamespace>
    <Description>Markdown 渲染原语：统一管线、表格横向滚动、frontmatter 剥离。仅依赖 Markdig，供宿主 UI 层复用。</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Markdig" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Seeing.Markdown.Tests" />
  </ItemGroup>
</Project>
```

`tests/primitives/Seeing.Markdown.Tests/Seeing.Markdown.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Markdown.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\primitives\Seeing.Markdown\Seeing.Markdown.csproj" />
  </ItemGroup>
</Project>
```

`Seeing.Agent.slnx` 两处补充：

```xml
  <Folder Name="/src/primitives/">
    <!-- 现有四条之后追加 -->
    <Project Path="src/primitives/Seeing.Markdown/Seeing.Markdown.csproj" />
  </Folder>

  <Folder Name="/tests/primitives/">
    <!-- 现有三条之后追加 -->
    <Project Path="tests/primitives/Seeing.Markdown.Tests/Seeing.Markdown.Tests.csproj" />
  </Folder>
```

- [ ] **Step 3：跑测试确认失败**

```bash
cd /e/Projects/CSharp/Seeing.Agent
dotnet build tests/primitives/Seeing.Markdown.Tests
```

Expected: FAIL — `CS0103: 当前上下文中不存在名称"MarkdownFrontMatter"`（类型尚未创建）

- [ ] **Step 4：实现 `MarkdownFrontMatter`**

`src/primitives/Seeing.Markdown/MarkdownFrontMatter.cs`：

```csharp
namespace Seeing.Markdown;

/// <summary>
/// YAML frontmatter 剥离。
/// <para>
/// 文档类 Markdown 常带 <c>---</c> 包裹的元数据头。直接交给 Markdig 时，
/// 闭合的 <c>---</c> 会被当成 setext 标题下划线，把整段元数据渲染成一个巨大标题
/// （用户看到 id/type/tags 满屏）。因此文档渲染必须先剥掉这一段。
/// </para>
/// <para>
/// <b>为什么不剥会话消息</b>：消息里开头的 <c>---</c> 通常是分隔线，
/// 当成元数据剥掉会丢内容。剥与不剥由调用方选入口区分（见 <see cref="MarkdownPipelineFactory"/>）。
/// </para>
/// </summary>
public static class MarkdownFrontMatter
{
    /// <summary>剥离 frontmatter；没有 frontmatter 时原样返回。</summary>
    /// <param name="content">原始 Markdown。</param>
    /// <returns>剥离后的正文；入参为 null/空或没有闭合分隔线时返回入参本身。</returns>
    public static string? Strip(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---")
            return content;

        for (var i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed is "---" or "...")
                return string.Join('\n', lines[(i + 1)..]);
        }

        // 没有闭合分隔线：不是 frontmatter，按原文渲染
        return content;
    }
}
```

- [ ] **Step 5：跑测试确认通过**

```bash
dotnet build tests/primitives/Seeing.Markdown.Tests && \
dotnet vstest tests/primitives/Seeing.Markdown.Tests/bin/Debug/net10.0/Seeing.Markdown.Tests.dll --TestCaseFilter:"FullyQualifiedName~FrontMatterTests"
```

Expected: PASS，7 个用例全通过（`Strip_WithNullOrEmpty_ShouldReturnSame` 是 Theory 2 个 case）

- [ ] **Step 6：Commit**

```bash
git add Seeing.Agent.slnx src/primitives/Seeing.Markdown tests/primitives/Seeing.Markdown.Tests
git commit -m "feat(markdown): 新增 Seeing.Markdown 原语包与 frontmatter 剥离

Markdig 的薄封装放在原语层（仅依赖 Markdig，零 Seeing.* 依赖），
以便宿主 UI（WebUI）、文件组件库（WebUI.Files）与工作台库
（WebUI.Workbench）都能引用而不产生反向依赖。

MarkdownFrontMatter.Strip 从 WebUI.Files 的 MarkdownRenderer.StripFrontMatter
迁入，行为不变：首行不是 --- 或无闭合分隔线时原样返回，避免吞掉正文分隔线。"
```

---

## Task 2：管线工厂 + 表格滚动扩展迁入

**Files:**
- Create: `src/primitives/Seeing.Markdown/MarkdownTableExtension.cs`（由 `samples/Seeing.Agent.WebUI/Rendering/Markdown/ScrollableTableExtension.cs` 迁入并改名）
- Create: `src/primitives/Seeing.Markdown/MarkdownPipelineFactory.cs`
- Create: `tests/primitives/Seeing.Markdown.Tests/MarkdownPipelineFactoryTests.cs`

**Interfaces:**
- Consumes: `MarkdownFrontMatter.Strip(string?)`
- Produces:
  - `Seeing.Markdown.MarkdownTableExtension.ContainerClass`（`const string = "md-table-scroll"`）
  - `Seeing.Markdown.MarkdownPipelineFactory.Render(string? content) → string`
  - `Seeing.Markdown.MarkdownPipelineFactory.RenderDocument(string? content) → string`

- [ ] **Step 1：写失败的测试**

`tests/primitives/Seeing.Markdown.Tests/MarkdownPipelineFactoryTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Markdown;

namespace Seeing.Markdown.Tests;

/// <summary>
/// 管线工厂契约：两条入口（保留 / 剥离 frontmatter）共用同一份渲染管线。
/// <para>
/// 这类缺口编译器看不见——CSS 选择器与 HTML 输出都是静默的，
/// 因此按渲染产物断言。历史缺陷：FileViewer / MemoryDetailPage 曾用裸
/// <c>new MarkdownPipelineBuilder().Build()</c>，表格渲染成一堆竖线。
/// </para>
/// </summary>
public class MarkdownPipelineFactoryTests
{
    private const string Table = """
        | 工具名 | 描述 |
        |---|---|
        | read_file | 读取文件 |
        """;

    [Fact]
    public void Render_Table_ShouldProduceTableElement()
    {
        MarkdownPipelineFactory.Render(Table)
            .Should().Contain("<table>", "裸管线不会注册表格扩展，表格会退化成一堆竖线");
    }

    [Fact]
    public void Render_Table_ShouldWrapInScrollContainer()
    {
        var html = MarkdownPipelineFactory.Render(Table);

        html.Should().Contain("<div class=\"md-table-scroll\">");
        (html.Split(MarkdownTableExtension.ContainerClass).Length - 1)
            .Should().Be(1, "每个表格只包一层，重复包裹会产生嵌套滚动条");
    }

    [Fact]
    public void Render_MultipleTables_ShouldEachGetOwnContainer()
    {
        var html = MarkdownPipelineFactory.Render($"{Table}\n\n中间段落\n\n{Table}");

        (html.Split(MarkdownTableExtension.ContainerClass).Length - 1).Should().Be(2);
    }

    [Fact]
    public void Render_WithoutTable_ShouldNotEmitScrollContainer()
    {
        MarkdownPipelineFactory.Render("只有段落与 `内联代码`。")
            .Should().NotContain(MarkdownTableExtension.ContainerClass);
    }

    [Fact]
    public void Render_RawHtml_ShouldBeEscaped()
    {
        var html = MarkdownPipelineFactory.Render("<script>alert(1)</script>");

        html.Should().NotContain("<script>", "DisableHtml 是既有安全契约，不得因管线合并而放松");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_NullOrEmpty_ShouldReturnEmpty(string? content)
    {
        MarkdownPipelineFactory.Render(content).Should().Be(string.Empty);
        MarkdownPipelineFactory.RenderDocument(content).Should().Be(string.Empty);
    }

    [Fact]
    public void Render_DocumentWithFrontMatter_ShouldKeepItAsContent()
    {
        var html = MarkdownPipelineFactory.Render("---\ntitle: x\n---\n正文");

        html.Should().NotContain("<h2>", "会话消息入口不剥 frontmatter，--- 是分隔线不是 setext 标题");
        html.Should().Contain("title", "消息入口把元数据当普通正文保留");
    }

    [Fact]
    public void RenderDocument_WithFrontMatter_ShouldStripMetadata()
    {
        var html = MarkdownPipelineFactory.RenderDocument("---\ntitle: 记忆\nid: abc\n---\n正文");

        html.Should().NotContain("title: 记忆", "文档入口必须先剥 frontmatter");
        html.Should().NotContain("id: abc");
        html.Should().Contain("正文");
    }

    [Fact]
    public void RenderDocument_Table_ShouldAlsoWrapInScrollContainer()
    {
        MarkdownPipelineFactory.RenderDocument(Table)
            .Should().Contain("<div class=\"md-table-scroll\">", "两条入口共用同一管线");
    }
}
```

- [ ] **Step 2：跑测试确认失败**

```bash
dotnet build tests/primitives/Seeing.Markdown.Tests
```

Expected: FAIL — `CS0103: 当前上下文中不存在名称"MarkdownPipelineFactory"`

- [ ] **Step 3：迁入表格扩展**

把 `samples/Seeing.Agent.WebUI/Rendering/Markdown/ScrollableTableExtension.cs` 的内容复制到 `src/primitives/Seeing.Markdown/MarkdownTableExtension.cs`，改三处：命名空间、类名、XML 注释里的 CSS 路径。完整内容：

```csharp
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Seeing.Markdown;

/// <summary>
/// 让 Markdown 表格横向可滚动。
/// <para>
/// <b>根因</b>：表格列多、单元格里又有较长的内联代码时，表格的 min-content 宽度会超过容器宽度，
/// 表格随之溢出。<c>&lt;table&gt;</c> 自身的 <c>overflow-x</c> 不裁剪自己的内容，救不了；
/// 而会话消息的最外层滚动容器 <c>.message-list-container</c> 是 <c>overflow-x: hidden</c>
/// （横向滚动由页面布局独占），于是溢出部分被直接剪掉——右侧列看不见，也没有任何滚动入口。
/// </para>
/// <para>
/// <b>做法</b>：渲染时给每个表格套一层 <c>div.md-table-scroll</c>，由这层容器承担横向滚动，
/// 表格宽度与列宽算法完全不变（放得下就 100% 铺满，放不下就按 min-content 展开并出现滚动条）。
/// 样式见宿主 CSS（会话消息在 <c>wwwroot/css/message-item.css</c>）；
/// 容器类名由 <see cref="ContainerClass"/> 单一来源提供，CSS 与契约测试共用。
/// </para>
/// </summary>
public sealed class MarkdownTableExtension : IMarkdownExtension
{
    /// <summary>横向滚动容器的类名（CSS 与契约测试共用同一常量）。</summary>
    public const string ContainerClass = "md-table-scroll";

    /// <inheritdoc/>
    public void Setup(MarkdownPipelineBuilder pipelineBuilder)
    {
        // 只换渲染器，不引入新语法
    }

    /// <inheritdoc/>
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
            return;

        // 必须在 UsePipeTables/UseGridTables 之后 Setup，否则会被它们注册的默认渲染器顶掉
        html.ObjectRenderers.ReplaceOrAdd<HtmlTableRenderer>(new ScrollableTableRenderer());
    }

    /// <summary>在默认的表格 HTML 外再包一层横向滚动容器。</summary>
    private sealed class ScrollableTableRenderer : HtmlTableRenderer
    {
        protected override void Write(HtmlRenderer renderer, Table table)
        {
            renderer.Write("<div class=\"").Write(ContainerClass).Write("\">");
            base.Write(renderer, table);
            renderer.Write("</div>");
        }
    }
}
```

- [ ] **Step 4：实现管线工厂**

`src/primitives/Seeing.Markdown/MarkdownPipelineFactory.cs`：

```csharp
using Markdig;

namespace Seeing.Markdown;

/// <summary>
/// Markdown 渲染管线的<b>唯一构造点</b>。
/// <para>
/// 历史上仓库里有 4 条独立管线，其中两条用的是裸 <c>new MarkdownPipelineBuilder().Build()</c>
/// （不注册表格扩展 → 表格渲染成一堆竖线）。收敛到一处后，所有渲染路径看到的
/// 语法支持与安全策略完全一致。
/// </para>
/// <para>
/// 两个入口的差别只有 frontmatter：会话消息里开头的 <c>---</c> 是分隔线（必须保留），
/// 文档 / 记忆文件里的 <c>---</c> 是元数据头（必须先剥，否则被当成 setext 标题下划线）。
/// </para>
/// </summary>
public static class MarkdownPipelineFactory
{
    /// <summary>
    /// 共享管线实例（不可变，线程安全）。
    /// <para>
    /// <c>DisableHtml</c> 是既有安全契约：禁止原始 HTML 透传，防注入。
    /// <see cref="MarkdownTableExtension"/> 必须排在 <c>UsePipeTables</c> / <c>UseGridTables</c>
    /// 之后，否则会被它们注册的默认表格渲染器顶掉。
    /// </para>
    /// </summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UseTaskLists()
        .UsePipeTables()
        .UseGridTables()
        .UseListExtras()
        .UseEmphasisExtras()
        .UseAutoIdentifiers()
        .UseCitations()
        .UseCustomContainers()
        .UseFooters()
        .UseFootnotes()
        .UseMathematics()
        .UseSmartyPants()
        .Use<MarkdownTableExtension>()
        .Build();

    /// <summary>
    /// 渲染普通 Markdown 内容（会话消息、工具输出等）。frontmatter 原样保留。
    /// </summary>
    /// <param name="content">原始 Markdown。</param>
    /// <returns>渲染后的 HTML；入参为 null/空时返回空串。</returns>
    public static string Render(string? content)
        => string.IsNullOrEmpty(content) ? string.Empty : Markdown.ToHtml(content, Pipeline);

    /// <summary>
    /// 渲染文档（文件预览 / 记忆详情 / 工作台）。先剥离 YAML frontmatter 再渲染。
    /// </summary>
    /// <param name="content">原始 Markdown。</param>
    /// <returns>渲染后的 HTML；入参为 null/空时返回空串。</returns>
    public static string RenderDocument(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var body = MarkdownFrontMatter.Strip(content);
        return Markdown.ToHtml(string.IsNullOrEmpty(body) ? content : body, Pipeline);
    }
}
```

**注意**：`Markdown.ToHtml` 与命名空间 `Seeing.Markdown` 同名会导致名称解析歧义（编译器会报 `CS0234`）。文件里已 `using Markdig;`，但类名 `Markdown` 与本命名空间同名时仍需**全限定**。把两处 `Markdown.ToHtml` 改成 `global::Markdig.Markdown.ToHtml`。

- [ ] **Step 5：跑测试确认通过**

```bash
dotnet build tests/primitives/Seeing.Markdown.Tests && \
dotnet vstest tests/primitives/Seeing.Markdown.Tests/bin/Debug/net10.0/Seeing.Markdown.Tests.dll
```

Expected: PASS，全部用例通过（FrontMatterTests 7 + MarkdownPipelineFactoryTests 10）

- [ ] **Step 6：Commit**

```bash
git add src/primitives/Seeing.Markdown tests/primitives/Seeing.Markdown.Tests
git commit -m "feat(markdown): 管线工厂 + 表格滚动扩展迁入原语包

MarkdownPipelineFactory 是全部渲染路径的唯一管线构造点，提供两个入口：
- Render：会话消息用，frontmatter 原样保留（--- 是分隔线）
- RenderDocument：文档用，先剥 frontmatter（--- 是元数据头）

MarkdownTableExtension 从 WebUI/Rendering/Markdown/ScrollableTableExtension.cs
迁入并改名，行为不变（表格包 div.md-table-scroll 承担横向滚动）。

命名空间 Seeing.Markdown 与 Markdig 的 Markdown 类同名，统一用
global::Markdig.Markdown.ToHtml 全限定调用。"
```

---

## Task 3：四个渲染入口切到工厂

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Rendering/Caching/MemoryRenderCache.cs:24,39-56,84,103`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileViewer.razor:3,37,66,68`
- Modify: `samples/Seeing.Agent.WebUI/Pages/MemoryDetailPage.razor:200-222`
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`（加项目引用）
- Modify: `samples/Seeing.Agent.WebUI.Files/Seeing.Agent.WebUI.Files.csproj`（加项目引用）
- Delete: `samples/Seeing.Agent.WebUI/Rendering/Markdown/ScrollableTableExtension.cs`
- Modify: `tests/apps/Seeing.Agent.WebUI.Tests/Rendering/MarkdownTableScrollTests.cs`

**Interfaces:**
- Consumes: `MarkdownPipelineFactory.Render` / `RenderDocument`
- Produces: 无新 API（仅替换调用点）

- [ ] **Step 1：加项目引用**

`samples/Seeing.Agent.WebUI.Files/Seeing.Agent.WebUI.Files.csproj` 的 `<ItemGroup>`（含 ProjectReference 的那个）追加：

```xml
    <ProjectReference Include="..\..\src\primitives\Seeing.Markdown\Seeing.Markdown.csproj" />
```

`samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj` 的第一个 `<ItemGroup>`（ProjectReference 组）追加：

```xml
    <ProjectReference Include="..\..\src\primitives\Seeing.Markdown\Seeing.Markdown.csproj" />
```

- [ ] **Step 2：改造 `MemoryRenderCache`**

删掉字段 `_markdownPipeline`（第 24 行）、构造函数里的管线构造（第 39–56 行），把两处 `Markdown.ToHtml(content, _markdownPipeline)`（第 84、103 行）改成 `MarkdownPipelineFactory.Render(content)`。

改造后的构造函数只剩一行赋值：

```csharp
    public MemoryRenderCache(ILogger<MemoryRenderCache> logger)
    {
        _logger = logger;
    }
```

文件头部 using 换成：

```csharp
using Seeing.Agent.WebUI.Rendering.Abstractions;
using Seeing.Markdown;
using System.Collections.Concurrent;
```

（`using Markdig;` 与 `using Seeing.Agent.WebUI.Rendering.MarkdownExtensions;` 都删掉。）

- [ ] **Step 3：改造 `FileViewer.razor`**

第 3 行 `@using Markdig` 改为 `@using Seeing.Markdown`；第 37 行保持不动（`RenderMarkdown(f.Content)`）；第 66、68 行改为：

```csharp
    private static string RenderMarkdown(string content) => MarkdownPipelineFactory.RenderDocument(content);
```

删掉 `s_pipeline` 字段。

- [ ] **Step 4：改造 `MemoryDetailPage.razor`**

替换整个 `RenderMarkdown` 方法（原第 200–222 行）：

```csharp
    private static string RenderMarkdown(string content)
    {
        if (string.IsNullOrEmpty(content)) return "<p>（空内容）</p>";

        try
        {
            // 记忆文件必带 YAML frontmatter，由 RenderDocument 内部剥离——
            // 否则闭合的 "---" 会被当成 setext 标题下划线，把整段元数据渲染成一个巨大标题。
            return Seeing.Markdown.MarkdownPipelineFactory.RenderDocument(content);
        }
        catch
        {
            // 降级为纯文本显示（HTML 转义）
            return "<pre>" + System.Net.WebUtility.HtmlEncode(content) + "</pre>";
        }
    }
```

并删掉该文件里不再使用的 `Seeing.Agent.Memory.Core.YamlParser` 调用与 `Markdig` 引用。

- [ ] **Step 5：删除迁走的扩展文件**

```bash
git rm samples/Seeing.Agent.WebUI/Rendering/Markdown/ScrollableTableExtension.cs
```

若目录变空则一并删除。

- [ ] **Step 6：改造 `MarkdownTableScrollTests`**

文件里 `using Seeing.Agent.WebUI.Rendering.MarkdownExtensions;` 改为 `using Seeing.Markdown;`，`ScrollableTableExtension.ContainerClass` 改为 `MarkdownTableExtension.ContainerClass`，`new MemoryRenderCache(...).GetOrCreateMarkdown(...)` 保留（缓存层仍在 WebUI）。原 `RenderedTable_ShouldBeWrappedInScrollContainer` / `ContentWithoutTable_ShouldNotEmitScrollContainer` / `MultipleTables_ShouldEachGetOwnContainer` 三个渲染用例此时与 `MarkdownPipelineFactoryTests` 重复，删掉它们，只保留两个 CSS 契约用例（`ScrollContainer_ShouldDeclareHorizontalOverflowInCss`、`TableRule_ShouldStillFillWidth`）与一个「缓存层走工厂」冒烟用例：

```csharp
    [Fact]
    public void CacheRender_ShouldUseSharedFactory()
    {
        var cache = new MemoryRenderCache(NullLogger<MemoryRenderCache>.Instance);
        var html = cache.GetOrCreateMarkdown("| a | b |\n|---|---|\n| 1 | 2 |", "k1");

        html.Should().Contain("<table>", "缓存层必须走共享管线，否则表格退化");
        html.Should().Contain(MarkdownTableExtension.ContainerClass);
    }
```

- [ ] **Step 7：跑测试**

```bash
dotnet build Seeing.Agent.slnx
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll
dotnet vstest tests/primitives/Seeing.Markdown.Tests/bin/Debug/net10.0/Seeing.Markdown.Tests.dll
```

Expected: 三个 DLL 全部 0 失败。特别确认 `MessageContentClassContractTests` 与 `MarkdownTableScrollTests` 通过。

- [ ] **Step 8：Commit**

```bash
git add -A
git commit -m "refactor(webui): 四处 Markdown 渲染入口收敛到 MarkdownPipelineFactory

收敛前有 4 条独立管线，其中 FileViewer 与 MemoryDetailPage 用裸
new MarkdownPipelineBuilder().Build()，不注册表格扩展 —— 实测
| a | b | 渲染成 <p>| a | b |<br/>|---|---|（竖线当普通字符）。

文件工作台里可编辑的 .md 走 Monaco 正常，但截断/非 UTF-8/超大文件
落进 FileViewer，那一刻表格就退化。记忆详情页同理。

改动：
- MemoryRenderCache：删内联管线，改用 Render（消息保留 frontmatter）
- FileViewer：裸管线 → RenderDocument
- MemoryDetailPage：裸管线 + 手动剥 frontmatter → RenderDocument
- 删除 WebUI/Rendering/Markdown/ScrollableTableExtension.cs（已迁入原语包）

MarkdownTableScrollTests 的渲染类断言已由 MarkdownPipelineFactoryTests 覆盖，
此处只保留 CSS 契约与「缓存层走共享管线」冒烟。"
```

---

## Task 4：`OpenFile.PreviewMode` 与 `TogglePreview`

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Services/FileTabsState.cs:52-57`（`OpenFile` 加字段）、`:88-115`（`Open` 设初值）、`:117+`（新增 `TogglePreview`）
- Test: `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTabsStateTests.cs`

**Interfaces:**
- Produces:
  - `OpenFile.PreviewMode`：`bool { get; set; }`
  - `FileTabsState.TogglePreview(FileResourceId id) → void`
  - `FileTabsState.SetPreview(FileResourceId id, bool preview) → void`

- [ ] **Step 1：写失败的测试**

在 `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTabsStateTests.cs` 追加（该类已存在，追加新用例即可）：

```csharp
    private static FileReadResult MarkdownResult(string path = "docs/a.md") => new(
        new FileResourceId("root", path),
        FileContentKind.Markdown,
        "text/markdown",
        "# hi",
        null,
        6,
        6,
        false,
        false,
        true,
        FileReadMode.Display);

    private static FileReadResult TextResult(string path = "src/a.cs") => new(
        new FileResourceId("root", path),
        FileContentKind.Text,
        "text/plain",
        "class A {}",
        null,
        10,
        10,
        false,
        false,
        true,
        FileReadMode.Display);

    [Fact]
    public void Open_MarkdownFile_ShouldDefaultToPreview()
    {
        var tabs = new FileTabsState();

        tabs.Open(MarkdownResult());

        tabs.OpenFiles[0].PreviewMode.Should().BeTrue("文档场景读多写少，默认预览");
    }

    [Fact]
    public void Open_TextFile_ShouldNotUsePreview()
    {
        var tabs = new FileTabsState();

        tabs.Open(TextResult());

        tabs.OpenFiles[0].PreviewMode.Should().BeFalse("非 Markdown 没有预览形态");
    }

    [Fact]
    public void TogglePreview_MarkdownFile_ShouldFlipAndRaiseOnce()
    {
        var tabs = new FileTabsState();
        tabs.Open(MarkdownResult());
        var id = tabs.OpenFiles[0].Id;
        var raised = 0;
        tabs.Changed += () => raised++;

        tabs.TogglePreview(id);
        tabs.OpenFiles[0].PreviewMode.Should().BeFalse();

        tabs.TogglePreview(id);
        tabs.OpenFiles[0].PreviewMode.Should().BeTrue();

        raised.Should().Be(2, "每次切换触发一次 Changed");
    }

    [Fact]
    public void TogglePreview_NonMarkdown_ShouldBeNoOp()
    {
        var tabs = new FileTabsState();
        tabs.Open(TextResult());
        var id = tabs.OpenFiles[0].Id;
        var raised = 0;
        tabs.Changed += () => raised++;

        tabs.TogglePreview(id);

        tabs.OpenFiles[0].PreviewMode.Should().BeFalse();
        raised.Should().Be(0, "无可切换的形态时不得空触发重渲染");
    }

    [Fact]
    public void TogglePreview_DeletedFile_ShouldBeNoOp()
    {
        var tabs = new FileTabsState();
        tabs.Open(MarkdownResult());
        var id = tabs.OpenFiles[0].Id;
        tabs.MarkDeleted(id);

        tabs.TogglePreview(id);

        tabs.OpenFiles[0].PreviewMode.Should().BeTrue("已删除文件不能切到预览");
    }

    [Fact]
    public void TogglePreview_ShouldOnlyAffectTargetTab()
    {
        var tabs = new FileTabsState();
        tabs.Open(MarkdownResult("a.md"));
        tabs.Open(MarkdownResult("b.md"));
        var first = tabs.OpenFiles[0].Id;

        tabs.TogglePreview(first);

        tabs.OpenFiles[0].PreviewMode.Should().BeFalse();
        tabs.OpenFiles[1].PreviewMode.Should().BeTrue("预览状态跟着标签走，不是全局开关");
    }

    [Fact]
    public void SetPreview_ExplicitFalse_ShouldOverrideDefault()
    {
        var tabs = new FileTabsState();
        tabs.Open(MarkdownResult());
        var id = tabs.OpenFiles[0].Id;

        tabs.SetPreview(id, preview: false);

        tabs.OpenFiles[0].PreviewMode.Should().BeFalse("agent 可用 preview=false 显式要求编辑态");
    }
```

**若 `FileReadResult` 构造签名不符，先读 `src/abstractions/Seeing.Agent.Abstractions/Files/FileReadResult.cs` 按实际参数顺序调整**（该 record 有 `Id, Kind, MimeType, Content, Encoding?, SizeBytes, TotalBytes, IsTruncated, IsSampled, IsEditable, Mode`）。

- [ ] **Step 2：跑测试确认失败**

```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Files.Tests
```

Expected: FAIL — `CS0117: "OpenFile"未包含"PreviewMode"的定义`

- [ ] **Step 3：实现**

在 `OpenFile` 类（`FileTabsState.cs`）末尾追加：

```csharp
    /// <summary>
    /// 是否处于预览态。仅对 <see cref="FileContentKind.Markdown"/> 有意义，新建标签时默认预览。
    /// <para>per-file 而非全局：同一工作台里 README 看渲染、笔记改源码是正常需求。</para>
    /// </summary>
    public bool PreviewMode { get; set; }
```

在 `FileTabsState.Open` 新建分支的 `new OpenFile { … }` 里加一行：

```csharp
                PreviewMode = file.Kind == FileContentKind.Markdown,
```

在 `FileTabsState` 类里 `Close` 方法之前追加：

```csharp
    /// <summary>
    /// 切换预览/编辑态。非 Markdown 或已删除文件不做任何事（也不触发 Changed）。
    /// </summary>
    /// <param name="id">目标标签。</param>
    public void TogglePreview(FileResourceId id) => SetPreview(id, !IsPreview(id));

    /// <summary>显式设置预览态；无变化或不可切换时不触发 Changed。</summary>
    /// <param name="id">目标标签。</param>
    /// <param name="preview">true = 预览，false = 编辑。</param>
    public void SetPreview(FileResourceId id, bool preview)
    {
        if (!_byKey.TryGetValue(Key(id), out var of)) return;
        if (of.Kind != FileContentKind.Markdown || of.IsDeleted) return;
        if (of.PreviewMode == preview) return;

        of.PreviewMode = preview;
        Changed?.Invoke();
    }

    private bool IsPreview(FileResourceId id)
        => _byKey.TryGetValue(Key(id), out var of) && of.PreviewMode;
```

- [ ] **Step 4：跑测试确认通过**

```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Files.Tests && \
dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileTabsStateTests"
```

Expected: PASS，含新增 7 个用例

- [ ] **Step 5：Commit**

```bash
git add samples/Seeing.Agent.WebUI.Files/Services/FileTabsState.cs tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTabsStateTests.cs
git commit -m "feat(files): OpenFile 增加 per-file PreviewMode

预览态跟着标签走而不是全局开关：同一工作台里 README 看渲染、
笔记改源码是正常需求，切走再切回不丢。

.md 新建标签默认预览（文档场景读多写少）；TogglePreview 对非
Markdown / 已删除文件是 no-op 且不触发 Changed，避免空重渲染。"
```

---

## Task 5：标签栏切换按钮 + 编辑器预览分支

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileTabs.razor:19-33`（保存按钮旁）
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor:21-32`（只读分支之后、diff 之后）
- Modify: `samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css`（按钮样式）

**Interfaces:**
- Consumes: `OpenFile.PreviewMode`、`FileTabsState.TogglePreview`

- [ ] **Step 1：`FileTabs.razor` 加切换按钮**

在 `@if (HasActiveEditable) { … }` 的保存按钮**之前**插入：

```razor
    @if (CanTogglePreview)
    {
        <button type="button"
                class="file-tab-preview"
                title="@(Active!.PreviewMode ? "切换到编辑" : "切换到预览")"
                @onclick="TogglePreviewClicked">
            @(Active!.PreviewMode ? "编辑" : "预览")
        </button>
    }
```

`@code` 区追加：

```csharp
    /// <summary>仅 Markdown 可切换；图片/二进制/已删除文件无预览形态。</summary>
    private bool CanTogglePreview =>
        Active is { Kind: FileContentKind.Markdown, IsDeleted: false };

    private void TogglePreviewClicked()
    {
        var f = Active;
        if (f is null) return;
        Tabs.TogglePreview(f.Id);
    }
```

文件头补 `@using Seeing.Agent.Abstractions.Files`（若尚未存在）。

- [ ] **Step 2：`FileEditor.razor` 插预览分支**

在 `else if (f.IsReadOnly) { … }` 分支之后、`else { … Monaco … }` 之前插入：

```razor
    else if (f.Kind is FileContentKind.Markdown && f.PreviewMode)
    {
        @* diff 优先于预览：PendingDiff 分支在上方已处理，这里只处理「无 diff 的预览态」 *@
        <FileViewer Tabs="Tabs" />
    }
```

- [ ] **Step 3：加按钮样式**

`samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css` 末尾追加：

```css
/* 预览/编辑切换：与保存按钮同高，紧邻排布；只在 Markdown 标签出现 */
.file-tab-preview {
    flex-shrink: 0;
    align-self: center;
    margin-left: 4px;
    padding: 2px 8px;
    font-size: 12px;
    line-height: 18px;
    color: var(--color-text-secondary, #595959);
    background: transparent;
    border: 1px solid var(--color-border, #d9d9d9);
    border-radius: 4px;
    cursor: pointer;
}

.file-tab-preview:hover {
    color: var(--color-primary, #1890ff);
    border-color: currentColor;
}
```

- [ ] **Step 4：构建 + 现有测试**

```bash
dotnet build samples/Seeing.Agent.WebUI.Files && \
dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll
```

Expected: 0 失败

- [ ] **Step 5：浏览器手验**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

打开会话 → 工作台下拉选「文件」→ 打开任意 `.md`。确认：
1. 默认显示渲染后的 HTML（表格有框线、可左右滚动）
2. 标签栏出现「编辑」按钮；点后变成 Monaco 源码视图，按钮文案变「预览」
3. 再点回预览态
4. 打开 `.cs` 文件：无切换按钮，直接 Monaco

- [ ] **Step 6：Commit**

```bash
git add samples/Seeing.Agent.WebUI.Files/Components/FileTabs.razor \
        samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor \
        samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css
git commit -m "feat(files): 文件工作台支持 Markdown 预览/编辑切换

标签栏在 Markdown 标签上显示「预览/编辑」切换按钮；FileEditor 在
PendingDiff 与只读分支之后、Monaco 之前插入预览分支，复用既有
FileViewer（它已实现 Markdown/图片/二进制/只读文本四种分支）。

diff 优先级最高，不受预览开关影响。"
```

---

## Task 6：`files` 声明的 `preview` 载荷 + 别名解析

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspaceModule.cs:50-95`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Ui/Workbench/WorkbenchRequestKinds.cs`
- Create: `samples/Seeing.Agent.WebUI.Workbench/Services/LegacyWorkbenchAlias.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Workbench.Tests/Services/LegacyWorkbenchAliasTests.cs`
- Modify: `samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchSurface.razor:233-257`

**Interfaces:**
- Produces:
  - `WorkbenchRequestKinds.LegacyMarkdownId`（`const string = "markdown"`）
  - `Seeing.Agent.WebUI.Workbench.LegacyWorkbenchAlias.Apply(string workbenchId, JsonElement? openPayload) → (string WorkbenchId, JsonElement? OpenPayload)`
  - `LegacyWorkbenchAlias.PreviewKeyName`（`const string = "preview"`）

- [ ] **Step 1：写失败的测试**

`tests/apps/Seeing.Agent.WebUI.Workbench.Tests/Services/LegacyWorkbenchAliasTests.cs`：

```csharp
using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.WebUI.Workbench;

namespace Seeing.Agent.WebUI.Workbench.Tests.Services;

/// <summary>
/// 历史工作台 id 别名：<c>markdown</c> 等价于 <c>files</c> + <c>preview=true</c>。
/// <para>
/// 保留别名的理由：模型已形成的调用习惯、历史对话里的工具调用记录、
/// 以及 <c>docs/architecture/41</c> 的示例都是 <c>workbenchId=markdown</c>。
/// 别放在宿主侧而不是工具侧——工具只查声明表，别名是「宿主怎么解释 id」的语义。
/// </para>
/// </summary>
public class LegacyWorkbenchAliasTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void Apply_LegacyMarkdownWithPath_ShouldBecomeFilesWithPreview()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("markdown", Json("""{"path":"docs/a.md"}"""));

        id.Should().Be("files");
        payload!.Value.GetProperty("path").GetString().Should().Be("docs/a.md");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Apply_LegacyMarkdownWithoutPayload_ShouldInjectPreviewOnly()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("markdown", null);

        id.Should().Be("files");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Apply_LegacyMarkdownWithExplicitPreviewFalse_ShouldRespectIt()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply(
            "markdown", Json("""{"path":"a.md","preview":false}"""));

        id.Should().Be("files");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeFalse(
            "显式要求编辑态时不得被别名覆盖");
    }

    [Fact]
    public void Apply_FilesId_ShouldPassThroughUnchanged()
    {
        var original = Json("""{"path":"a.md","preview":false}""");

        var (id, payload) = LegacyWorkbenchAlias.Apply("files", original);

        id.Should().Be("files");
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Apply_OtherId_ShouldPassThroughUnchanged()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("video", null);

        id.Should().Be("video");
        payload.Should().BeNull();
    }

    [Fact]
    public void Apply_LegacyMarkdownWithNonObjectPayload_ShouldStillInjectPreview()
    {
        var (id, payload) = LegacyWorkbenchAlias.Apply("markdown", Json("\"not-an-object\""));

        id.Should().Be("files");
        payload!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        payload!.Value.GetProperty("preview").GetBoolean().Should().BeTrue();
    }
}
```

- [ ] **Step 2：跑测试确认失败**

```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Workbench.Tests
```

Expected: FAIL — `CS0246: 未能找到类型或命名空间名"LegacyWorkbenchAlias"`

- [ ] **Step 3：加常量**

`src/abstractions/Seeing.Agent.Abstractions/Ui/Workbench/WorkbenchRequestKinds.cs` 的 `ReservedRegistryId` 之后追加：

```csharp
    /// <summary>
    /// 已合并的历史工作台 id：等价于 <c>files</c> + <c>openPayload.preview=true</c>。
    /// <para>保留它是为了不打破模型既有的 <c>open_workbench(workbenchId=markdown)</c> 调用习惯。</para>
    /// </summary>
    public const string LegacyMarkdownId = "markdown";
```

- [ ] **Step 4：实现别名解析**

`samples/Seeing.Agent.WebUI.Workbench/Services/LegacyWorkbenchAlias.cs`：

```csharp
using Seeing.Agent.Abstractions.Ui.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>
/// 历史工作台 id 别名解析。
/// <para>
/// <c>markdown</c> 工作台已合并进 <c>files</c>（作为预览态）。为了让模型既有的
/// <c>open_workbench(workbenchId=markdown, payload={path})</c> 调用继续工作，
/// 在宿主侧把它解释为 <c>files</c> + <c>preview=true</c>。
/// </para>
/// <para>
/// <b>为什么不放工具侧</b>：<c>OpenWorkbenchTool</c> 的五道门闸是对声明表的通用查询。
/// 把别名塞进工具会让每道门闸各多一个特例分支，且工具拿不到「宿主怎么解释这个 id」
/// 的知识。别名是渲染层语义，归宿主表面。
/// </para>
/// </summary>
public static class LegacyWorkbenchAlias
{
    /// <summary>目标工作台 id（别名解析后）。</summary>
    public const string TargetWorkbenchId = "files";

    /// <summary>预览标志的载荷键名。</summary>
    public const string PreviewKeyName = "preview";

    /// <summary>解析别名；非别名 id 原样返回。</summary>
    /// <param name="workbenchId">请求里的工作台 id。</param>
    /// <param name="openPayload">请求里的打开载荷（可空）。</param>
    /// <returns>解析后的 id 与载荷。别名命中时载荷被保证是对象且含 <c>preview</c>（除非调用方显式给了值）。</returns>
    public static (string WorkbenchId, JsonElement? OpenPayload) Apply(
        string workbenchId, JsonElement? openPayload)
    {
        if (!string.Equals(workbenchId, WorkbenchRequestKinds.LegacyMarkdownId, StringComparison.Ordinal))
            return (workbenchId, openPayload);

        return (TargetWorkbenchId, WithPreviewTrue(openPayload));
    }

    /// <summary>把载荷规范化为对象并补 <c>preview: true</c>（已有该键则保留原值）。</summary>
    private static JsonElement WithPreviewTrue(JsonElement? payload)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (payload is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
                properties[property.Name] = property.Value.Clone();
        }

        if (!properties.ContainsKey(PreviewKeyName))
            properties[PreviewKeyName] = JsonSerializer.SerializeToElement(true);

        return JsonSerializer.SerializeToElement(properties);
    }
}
```

- [ ] **Step 5：接进 `WorkbenchSurface.Open`**

把 `ComputeRegistryResponse` 里的 `WorkbenchRequestKinds.RegistryOpen => Open(request)` 保持不变，在 `Open(HostRequest request)` 方法内、取出 `workbenchId` 之后、`Workbenches.FirstOrDefault(...)` **之前**插入：

```csharp
        // 历史别名：markdown → files + preview=true（见 LegacyWorkbenchAlias）
        var aliased = LegacyWorkbenchAlias.Apply(workbenchId, null);
        workbenchId = aliased.WorkbenchId;
```

并在下方原本读取 `openPayload` 的位置，把结果过一遍别名：

```csharp
        System.Text.Json.JsonElement? openPayload = null;
        if (payload.TryGetProperty("openPayload", out var openElement)
            && openElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            openPayload = openElement;
        }

        // 别名解析需要看到载荷才能保留显式的 preview=false
        openPayload = LegacyWorkbenchAlias.Apply(
            payload.TryGetProperty("workbenchId", out var rawId) ? rawId.GetString() ?? string.Empty : string.Empty,
            openPayload).OpenPayload;
```

**注意**：上面两段要合并成一次调用更清晰——直接改写 `Open()` 的开头为：

```csharp
        if (request.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object } payload
            || !payload.TryGetProperty("workbenchId", out var idElement)
            || idElement.ValueKind != System.Text.Json.JsonValueKind.String
            || idElement.GetString() is not { Length: > 0 } rawWorkbenchId)
        {
            return Failed(request, "缺少 workbenchId");
        }

        System.Text.Json.JsonElement? rawPayload = null;
        if (payload.TryGetProperty("openPayload", out var openElement)
            && openElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            rawPayload = openElement;
        }

        // 历史别名：markdown → files + preview=true（保留显式 preview=false）
        var (workbenchId, openPayload) = LegacyWorkbenchAlias.Apply(rawWorkbenchId, rawPayload);

        var contribution = Workbenches.FirstOrDefault(w =>
            string.Equals(w.Id, workbenchId, StringComparison.Ordinal));
```

下方原第 260–265 行读取 `openPayload` 的代码块删掉（已在上方完成）。

- [ ] **Step 6：跑测试确认通过**

```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Workbench.Tests && \
dotnet vstest tests/apps/Seeing.Agent.WebUI.Workbench.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Workbench.Tests.dll --TestCaseFilter:"FullyQualifiedName~LegacyWorkbenchAliasTests"
```

Expected: PASS，6 个用例

- [ ] **Step 7：Commit**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Ui/Workbench/WorkbenchRequestKinds.cs \
        samples/Seeing.Agent.WebUI.Workbench/Services/LegacyWorkbenchAlias.cs \
        samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchSurface.razor \
        tests/apps/Seeing.Agent.WebUI.Workbench.Tests/Services/LegacyWorkbenchAliasTests.cs
git commit -m "feat(workbench): workbenchId=markdown 别名路由到 files + preview

模型已形成的调用习惯、历史对话里的工具调用记录、docs/architecture/41
的示例都是 workbenchId=markdown。别名让这些继续工作。

别名放宿主侧（WorkbenchSurface）而不是工具侧：OpenWorkbenchTool 的
五道门闸是对声明表的通用查询，塞特例会让每道门闸各多一个分支，
且工具拿不到「宿主怎么解释 id」的知识。

显式 preview=false 不被覆盖——agent 可以要求以编辑态打开。"
```

---

## Task 7：`files` 声明增加 preview 键 + 删除 markdown 声明

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspaceModule.cs:39-95`

**Interfaces:**
- Produces: `files` 工作台的 `OpenPayloadSchema` 新增可选 `preview: boolean`；`DescribeUi()` 只返回一条声明

- [ ] **Step 1：改声明**

`FileWorkspaceModule.DescribeUi()` 的 `files` 声明里，`OpenPayloadSchema` 的 `properties` 增加：

```csharp
                    preview = new { type = "boolean", description = "是否以预览态打开（仅 Markdown 文件有效，默认 true）" },
```

`required` 保持 `new[] { "path" }`。

删除整个 `markdown` 的 `WorkbenchContribution` 块（第 73–95 行），`DescribeUi()` 返回值从数组两项变为一项。

同时把方法头注释改为：

```csharp
    /// <summary>
    /// 声明「文件」工作台。
    /// <para>
    /// <b>视图键而不是组件类型</b>：能力包不能引用 <c>samples/Seeing.Agent.WebUI.Files</c>
    /// 的 Razor 组件；宿主在 <c>WebUiWorkbenchViews</c> 里把该键映射到组件。
    /// </para>
    /// <para>
    /// Markdown 预览已合并进本工作台（<c>preview</c> 载荷键 + 标签栏切换按钮）。
    /// 历史 id <c>markdown</c> 由宿主 <c>LegacyWorkbenchAlias</c> 别名为 <c>files</c> + <c>preview=true</c>。
    /// </para>
    /// </summary>
```

- [ ] **Step 2：跑相关测试**

```bash
dotnet build tests/capabilities/Seeing.Agent.Files.Tests && \
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll
```

Expected: PASS（若存在断言「两条声明」的用例会失败，见 Task 9 统一处理）

- [ ] **Step 3：Commit**

```bash
git add src/capabilities/Seeing.Agent.Files/FileWorkspaceModule.cs
git commit -m "refactor(files): 删除 markdown 工作台声明，files 载荷增加 preview 键

markdown 的渲染能力已合并进 files（Task 4/5/6）。声明表从两条收敛为一条，
list_workbenches 不再单列 markdown；历史 id 由宿主别名兼容。

preview 是可选键（required 仍只有 path），仅对 Markdown 生效，缺省 true。"
```

---

## Task 8：`FilesWorkbench` 补齐 `Context.Report` 与 `ViewState` 恢复

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Workbenches/FilesWorkbench.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor:344-352`（`OpenPathAsync` 增加 preview 参数）
- Modify: `samples/Seeing.Agent.WebUI.Files/Services/FilePanelState.cs`（`FileOpenRequest` 增加 `Preview`）

**Interfaces:**
- Consumes: `FileTabsState.SetPreview`、`LegacyWorkbenchAlias.PreviewKeyName`
- Produces:
  - `FileOpenRequest(FileResourceId Id, int? Line, int? Column, bool? Preview)`（末尾新增可空参数）
  - `FilePanelHost.OpenPathAsync(string path, int? line, int? column, bool? preview)`

- [ ] **Step 1：`FileOpenRequest` 增加 Preview**

`FilePanelState.cs` 的 record 改为：

```csharp
/// <summary>打开文件请求（可携带跳转位置，line/column 均 1-based，可空）。</summary>
/// <param name="Id">目标资源标识。</param>
/// <param name="Line">目标行（1-based）。</param>
/// <param name="Column">目标列（1-based）。</param>
/// <param name="Preview">是否预览（null = 用标签默认值；仅 Markdown 有意义）。</param>
public sealed record FileOpenRequest(FileResourceId Id, int? Line = null, int? Column = null, bool? Preview = null);
```

已有调用点（`RequestOpen` 的调用方）不用改——新参数有默认值。

- [ ] **Step 2：`FilePanelHost.OpenPathAsync` 支持 preview**

把第 344–352 行的方法改为：

```csharp
    /// <summary>
    /// 宿主桥接入口：按路径打开（工作台打开载荷用）。
    /// <para>路径经工作区解析为资源标识，与文件树走同一套根规则。</para>
    /// <param name="path">文件路径（绝对或相对工作目录）。</param>
    /// <param name="line">跳转行号（1-based，可空）。</param>
    /// <param name="column">跳转列号（1-based，可空）。</param>
    /// <param name="preview">是否预览（null = 用标签默认值）。</param>
    /// </summary>
    public async Task OpenPathAsync(string path, int? line = null, int? column = null, bool? preview = null)
    {
        if (_workspace is null || string.IsNullOrWhiteSpace(path))
            return;

        var id = _workspace.GetResourceId(path);
        await OpenOrActivateAsync(id, refreshIfActive: false, line, column, preview);
    }
```

`OpenOrActivateAsync` 签名末尾加 `bool? preview = null`，并在**两个分支都应用**：

```csharp
    public async Task OpenOrActivateAsync(
        FileResourceId id, bool refreshIfActive, int? line = null, int? column = null, bool? preview = null)
    {
        if (_workspace is null) return;

        var key = id.ToString();
        var existing = _tabs.OpenFiles.FirstOrDefault(x => x.Id.ToString() == key);
        if (existing is not null)
        {
            if (string.Equals(_tabs.ActiveKey, key, StringComparison.Ordinal))
            {
                if (refreshIfActive)
                    await RefreshAsync(id);
            }
            else
            {
                _tabs.Activate(id);
            }
        }
        else
        {
            var (file, error) = await TryReadAsync(id);
            if (file is null)
            {
                SetError(error);
                return;
            }
            var binary = await ReadBinaryIfNeededAsync(file);
            _tabs.Open(file, binary);
        }

        // 显式 preview 覆盖标签默认值（agent 可要求以编辑态打开 .md）
        if (preview is { } wantPreview)
            _tabs.SetPreview(id, wantPreview);

        SetError(null);
        if (line is not null)
            await NavigateToAsync(line.Value, column);
    }
```

`ConsumePendingOpenAsync` 里同步补 `pending.Preview`：

```csharp
        await OpenOrActivateAsync(pending.Id, refreshIfActive: true, pending.Line, pending.Column, pending.Preview);
```

**注意**：上面用到 `PanelState.ActiveFile`——该成员当前不存在，需在 Step 3 一并补上。

- [ ] **Step 3a：`FilePanelState` 增加激活标签通知**

在 `FilePanelState` 里追加：

```csharp
    /// <summary>当前激活标签（由 FilePanelHost 在标签变化时同步；null = 无打开文件）。</summary>
    public OpenFile? ActiveFile { get; private set; }

    /// <summary>由宿主同步当前激活标签；变化时触发 <see cref="Changed"/>。</summary>
    public void SetActiveFile(OpenFile? file)
    {
        if (ReferenceEquals(ActiveFile, file)) return;
        ActiveFile = file;
        Changed?.Invoke();
    }
```

`FilePanelHost.OnTabsChanged` 里补一行（放在 `SyncTreeSelection()` 之后、`ClosePanelIfEmpty()` 之前）：

```csharp
        // 工作台视图据此上报「正在看哪个文件」，无需反查 FileTabsState
        PanelState.SetActiveFile(_tabs.OpenFiles.FirstOrDefault(x => x.Id.ToString() == _tabs.ActiveKey));
```

**不会死循环**：`SetActiveFile` 触发的 `Changed` 会让 `OnPanelStateChanged` 调 `ConsumePendingOpenAsync`，后者在 `PendingOpen` 为 null 时立即返回、不触发任何事件。

- [ ] **Step 3b：`FilesWorkbench` 读 preview 载荷 + 上报上下文**

把 `FilesWorkbench.razor` 的 `OnAfterRenderAsync` 与新增的上报逻辑改为：

```razor
@using Seeing.Agent.Abstractions.Files
@using Seeing.Agent.Abstractions.Ui.Workbench
@using Seeing.Agent.WebUI.Files.Components
@using Seeing.Agent.WebUI.Files.Services
@using Seeing.Agent.WebUI.Workbench
@inject FilePanelState PanelState
@implements IDisposable

@*
    文件工作台视图：把既有 FilePanelHost 包成可停靠工作台。
    工作台平台负责开合 / 停靠侧 / 宽度 / 标签；本视图只负责文件浏览与编辑。
    Markdown 预览已并入本工作台（标签栏切换按钮），不再有独立的 markdown 工作台。
*@
<CascadingValue Name="SessionId" Value="@Context.SessionId">
    <FilePanelHost @ref="_host" OnRequestClose="@OnCloseSelf" />
</CascadingValue>

@code {
    /// <summary>工作台视图上下文（平台注入）。</summary>
    [Parameter, EditorRequired] public WorkbenchViewContext Context { get; set; } = default!;

    private FilePanelHost? _host;
    private bool _openPayloadHandled;
    private string? _lastReportedPath;
    private bool? _lastReportedPreview;

    protected override void OnInitialized()
    {
        PanelState.Changed += OnPanelStateChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // 打开载荷：{ path?, line?, column?, preview? }（由 open_workbench / open_file 带来）
        if (!_openPayloadHandled)
        {
            _openPayloadHandled = true;
            if (ReadString(Context.OpenPayload, "path") is { Length: > 0 } path && _host is not null)
            {
                await _host.OpenPathAsync(
                    path,
                    ReadInt(Context.OpenPayload, "line"),
                    ReadInt(Context.OpenPayload, "column"),
                    ReadBool(Context.OpenPayload, LegacyWorkbenchAlias.PreviewKeyName));
            }
        }

        // 刷新恢复：上次查看的文件
        if (!_restoredViewState && Context.ViewState is { } state
            && state.TryGetValue("path", out var saved)
            && !string.IsNullOrWhiteSpace(saved)
            && _host is not null)
        {
            _restoredViewState = true;
            var preview = state.TryGetValue(LegacyWorkbenchAlias.PreviewKeyName, out var p) && p == "1";
            await _host.OpenPathAsync(saved, preview: preview);
        }

        // 待处理请求可能在挂载后才到达（agent 先请求、工作台后打开）
        await ConsumePendingOpenAsync();
        ReportContext();
    }

    private bool _restoredViewState;

    private async Task ConsumePendingOpenAsync()
    {
        if (_host is null || PanelState.PendingOpen is not { } pending)
            return;

        PanelState.ConsumePendingOpen();
        await _host.OpenOrActivateAsync(pending.Id, refreshIfActive: true, pending.Line, pending.Column, pending.Preview);
    }

    /// <summary>激活标签变化时上报「用户正在看/编辑哪个文件」，供 agent 感知。</summary>
    private void ReportContext()
    {
        var active = PanelState.ActiveFile;
        if (active is null)
            return;

        var path = active.Id.RelativePath;
        var preview = active.PreviewMode;
        if (string.Equals(_lastReportedPath, path, StringComparison.Ordinal) && _lastReportedPreview == preview)
            return;

        _lastReportedPath = path;
        _lastReportedPreview = preview;

        Context.Report(new WorkbenchViewReport
        {
            ViewState = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = path,
                [LegacyWorkbenchAlias.PreviewKeyName] = preview ? "1" : "0"
            },
            ContextSummary = preview ? $"正在预览 {path}" : $"正在编辑 {path}"
        });
    }

    private void OnPanelStateChanged() => _ = InvokeAsync(async () =>
    {
        await ConsumePendingOpenAsync();
        ReportContext();
        StateHasChanged();
    });

    private void OnCloseSelf() => Context.Close();

    private static string? ReadString(System.Text.Json.JsonElement? payload, string name)
        => payload is { ValueKind: System.Text.Json.JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(System.Text.Json.JsonElement? payload, string name)
        => payload is { ValueKind: System.Text.Json.JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? ReadBool(System.Text.Json.JsonElement? payload, string name)
        => payload is { ValueKind: System.Text.Json.JsonValueKind.Object } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public void Dispose()
    {
        PanelState.Changed -= OnPanelStateChanged;
    }
}
```

**注意**：上面用到的 `PanelState.ActiveFile` 在 Step 3a 中新增，此处已改完。

- [ ] **Step 4：构建 + 手验上报**

```bash
dotnet build samples/Seeing.Agent.WebUI.Files && dotnet run --project samples/Seeing.Agent.WebUI
```

打开文件工作台并点开一个 `.md`，在浏览器 DevTools 观察 SignalR 帧里出现 `WorkbenchViewReport`（或直接看 agent 侧上下文源是否叙述「正在预览 a.md」）。再点「编辑」，确认摘要变成「正在编辑 a.md」。

- [ ] **Step 5：Commit**

```bash
git add samples/Seeing.Agent.WebUI.Files/Workbenches/FilesWorkbench.razor \
        samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor \
        samples/Seeing.Agent.WebUI.Files/Services/FilePanelState.cs
git commit -m "feat(files): 文件工作台补齐上下文上报与刷新恢复

删除独立 MD 工作台会丢掉「用户正在看哪个文档」这个上下文
（MarkdownWorkbench 原先调 Context.Report，FilesWorkbench 从不调）。

补齐：
- FilePanelState.ActiveFile + SetActiveFile：宿主同步激活标签，
  FilesWorkbench 不必反查 FileTabsState
- FilesWorkbench.ReportContext：路径或预览态变化时上报
  ViewState{path, preview} + ContextSummary「正在预览/编辑 X」
- FilesWorkbench 读 ViewState 恢复上次查看的文件与模式
- FileOpenRequest / OpenPathAsync / OpenOrActivateAsync 支持 preview 参数"
```

---

## Task 9：同步现有测试与图标映射

**Files:**
- Modify: `tests/capabilities/Seeing.Agent.Workbench.Tests/WorkbenchRequestKindsTests.cs:11`
- Modify: `tests/capabilities/Seeing.Agent.Workbench.Tests/WorkbenchSessionStateStoreTests.cs:22,27,101,104`
- Modify: `tests/capabilities/Seeing.Agent.Workbench.Tests/WorkbenchContextSourceTests.cs:80,91,128,131,138,141,165,168,172`
- Modify: `tests/capabilities/Seeing.Agent.Workbench.Tests/WorkbenchToolsTests.cs`（若有 markdown 断言）
- Modify: `samples/Seeing.Agent.WebUI/Components/WorkbenchPicker.razor:126-131`
- Modify: `samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchTabStrip.razor:82-87`

**Interfaces:**
- 无新 API

- [ ] **Step 1：逐个确认引用了 markdown 的断言**

```bash
rg -n 'markdown' tests/capabilities/Seeing.Agent.Workbench.Tests tests/apps/Seeing.Agent.WebUI.Workbench.Tests
```

预期命中上述文件。对每一处判断：
- **测 kind 往返**（`WorkbenchRequestKindsTests`）：`"markdown"` 作为**任意字符串**参与往返，与工作台声明无关 → **保留不动**（`For("markdown","scroll")` 仍然合法）。
- **测实例/快照/上下文源**（其余三个）：`markdown` 是「另一个工作台的样本」，删声明后会让人误以为它还独立存在 → **换成 `video`**（一个明确不存在的假设工作台，语义清晰）。

- [ ] **Step 2：替换样本 id**

`WorkbenchSessionStateStoreTests.cs`：

```csharp
        store.Update("s1", [Snapshot("files"), Snapshot("video", active: false)]);   // 原 Snapshot("markdown", …)
        snapshot[1].Key.WorkbenchId.Should().Be("video");                            // 原 markdown
        store.Update("s2", [Snapshot("video")]);                                     // 原 markdown
        store.GetSnapshot("s2").Single().Key.WorkbenchId.Should().Be("video");        // 原 markdown
```

`WorkbenchContextSourceTests.cs`：

```csharp
            Snapshot("video", dock: WorkbenchDock.Right),          // 原 markdown
        ids.Should().Equal(["files", "alpha", "video"], "左 dock 在前，同 dock 按 id 序数序");   // 原 [..., "markdown"]
        store.Update("s1", [Snapshot("files"), Snapshot("video")]);                              // 原 markdown
        source.Render(before, after).Should().Contain("打开了").And.Contain("video");            // 原 markdown
        store.Update("s1", [Snapshot("files", title: "文件"), Snapshot("video")]);              // 原 markdown
        store.Update("s1", [Snapshot("video")]);                                                // 原 markdown
        store.Update("s1", [Snapshot("files", active: true), Snapshot("video", active: false)]); // 原 markdown
        store.Update("s1", [Snapshot("files", active: false), Snapshot("video", active: true)]); // 原 markdown
        text.Should().Contain("切到").And.Contain("video");                                      // 原 markdown
```

`WorkbenchToolsTests.cs`：若断言里出现「当前可用：`files`, `markdown`」之类文案，改为「当前可用：`files`」。

- [ ] **Step 3：删图标映射**

`WorkbenchPicker.razor` 的 `IconFor`：

```csharp
    private static string IconFor(string workbenchId) => workbenchId switch
    {
        "files" => "📁",
        _ => "▦"
    };
```

`WorkbenchTabStrip.razor` 的 `IconFor`：

```csharp
    private static string IconFor(string workbenchId) => workbenchId switch
    {
        "files" => "📁",
        _ => ""
    };
```

- [ ] **Step 4：跑测试**

```bash
dotnet build Seeing.Agent.slnx
dotnet vstest tests/capabilities/Seeing.Agent.Workbench.Tests/bin/Debug/net10.0/Seeing.Agent.Workbench.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Workbench.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Workbench.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll
```

Expected: 0 失败

- [ ] **Step 5：Commit**

```bash
git add tests samples/Seeing.Agent.WebUI/Components/WorkbenchPicker.razor \
        samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchTabStrip.razor
git commit -m "test(workbench): markdown 不再作为独立工作台样本

实例/快照/上下文源的测试把 markdown 当「另一个工作台」的样本，
声明删除后会让人误以为它还独立存在。改为 video（明确不存在的假设
工作台），语义更清楚。

WorkbenchRequestKindsTests 的 markdown 是纯粹的 kind 往返字符串，
与声明无关，保留。

图标映射删掉 markdown 分支：别名路由后不会有 markdown 实例。"
```

---

## Task 10：删除 Markdown 工作台与相关资产

**Files:**
- Delete: `samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownWorkbench.razor`
- Delete: `samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownRenderer.cs`
- Modify: `samples/Seeing.Agent.WebUI/Shell/WebUiWorkbenchViews.cs:29-31`
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/workbench.css:85-190`

**Interfaces:**
- 无新 API

- [ ] **Step 1：删组件与渲染器**

```bash
git rm samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownWorkbench.razor \
       samples/Seeing.Agent.WebUI.Files/Workbenches/MarkdownRenderer.cs
```

- [ ] **Step 2：删视图登记**

`WebUiWorkbenchViews.RegisterBuiltIns` 改为：

```csharp
    public static void RegisterBuiltIns(WorkbenchViewRegistry registry, WebUiViewResolver views)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(views);

        Register(registry, views, "files", typeof(FilesWorkbench));
    }
```

删掉 `using Seeing.Agent.WebUI.Files.Workbenches;` 中未使用的部分（`FilesWorkbench` 仍在同命名空间，保留）。

- [ ] **Step 3：删 CSS**

`workbench.css` 删除第 85 行 `/* ===== Markdown 预览工作台 ===== */` 到第 190 行（`.md-workbench-retry` 规则结束）之后的空行。删除后确认文件中不再有 `.md-workbench`：

```bash
rg -n 'md-workbench' samples/Seeing.Agent.WebUI/wwwroot/css/workbench.css
```

Expected: 无输出

- [ ] **Step 4：全仓确认无残留引用**

```bash
rg -n 'MarkdownWorkbench|MarkdownRenderer|md-workbench' src samples tests
```

Expected: 无输出（`MarkdownRenderer` 在 `Seeing.Markdown` 里叫 `MarkdownPipelineFactory`，不受影响）

- [ ] **Step 5：构建 + 全量测试**

```bash
dotnet build Seeing.Agent.slnx
```

Expected: 0 错误

- [ ] **Step 6：Commit**

```bash
git add -A
git commit -m "chore(files): 删除 Markdown 预览工作台

能力已并入文件工作台：
- 渲染 → MarkdownPipelineFactory.RenderDocument（原语包）
- 入口 → files 工作台的 preview 载荷键 + 标签栏切换按钮
- 上下文 → FilesWorkbench.ReportContext
- 兼容 → LegacyWorkbenchAlias（workbenchId=markdown）

删除 MarkdownWorkbench.razor（151 行）、MarkdownRenderer.cs（63 行）、
WebUiWorkbenchViews 的 markdown 登记、workbench.css 的 md-workbench-*
规则（第 85–190 行）。"
```

---

## Task 11：更新架构文档

**Files:**
- Modify: `docs/architecture/41-workbench-plugin-platform-release-notes.md:69,131,239`
- Modify: `docs/architecture/README.md`（若有工作台清单）
- Modify: `AGENTS.md`（WHERE TO LOOK 表的工作台行）

**Interfaces:**
- 无代码改动

- [ ] **Step 1：更新 41 号文档**

第 69 行改为：

```markdown
`FileWorkspaceModule` 同时实现 `IUiContributor`，声明「文件」工作台（`files`），`Requires = ["files.workspace"]`。Markdown 预览已合并进该工作台（`preview` 载荷键 + 标签栏切换），历史 id `markdown` 由宿主 `LegacyWorkbenchAlias` 别名兼容。因此：
```

第 131 行的失败文案示例改为：

```markdown
| ② | id 未知 | 「未知工作台 'x'。当前可用：`files`」 |
```

第 239 行验收项改为：

```markdown
- 首页 200；会话页工具栏出现「工作台」下拉，列出「文件」；
```

文末追加一节：

```markdown
## 后续变更：Markdown 预览并入文件工作台（2026-11-11）

调研确认 Monaco 0.55.1 不提供 Markdown 预览（`monaco` 顶层无 `markdown` 命名空间、
`editor.colorize` 只产出逐 token 的 `<span>`、无 webview 宿主），MD 工作台无法由
Monaco 替代。但仓库同时存在 4 条 Markdown 管线，其中 `FileViewer` 与
`MemoryDetailPage` 用裸 `MarkdownPipelineBuilder().Build()`，表格渲染成一堆竖线。

- 新增原语包 `Seeing.Markdown`：`Render`（消息，保留 frontmatter）/
  `RenderDocument`（文档，剥 frontmatter）两个入口，四处渲染全部收敛。
- `OpenFile.PreviewMode`（per-file，`.md` 默认预览）+ 标签栏「预览/编辑」按钮 +
  `FileEditor` 在 diff 之后、Monaco 之前插入预览分支（复用 `FileViewer`）。
- `workbenchId=markdown` 由宿主 `LegacyWorkbenchAlias` 解析为 `files + preview=true`。
- `FilesWorkbench` 补 `Context.Report`，避免删除 MD 工作台后丢失「用户正在看哪个文档」。

设计详见 `docs/superpowers/specs/2026-11-11-unified-markdown-pipeline-and-workbench-merge-design.md`。
```

- [ ] **Step 2：更新 AGENTS.md WHERE TO LOOK**

找到「会话工作台（可停靠视图平台）」那一行，在末尾追加：

```markdown
；Markdown 预览已并入 `files`（`preview` 载荷键 + 标签栏切换），历史 id `markdown` 由 `LegacyWorkbenchAlias` 别名兼容
```

并在「文件浏览器」相关行补充 Markdown 渲染入口：

```markdown
| Markdown 渲染 | `src/primitives/Seeing.Markdown/`（`MarkdownPipelineFactory`） | **唯一管线构造点**：`Render`（消息，保留 frontmatter）/ `RenderDocument`（文档，剥 frontmatter）；宿主 CSS 需声明 `.md-table-scroll` 横向滚动 |
```

- [ ] **Step 3：Commit**

```bash
git add docs AGENTS.md
git commit -m "docs: 记录 Markdown 管线统一与工作台合并

41 号发布说明更新工作台清单（只剩 files）、失败文案示例、验收项，
并追加本次变更章节（含 Monaco 调研结论与 4 条管线收敛过程）。

AGENTS.md 的 WHERE TO LOOK 增加 Markdown 渲染入口行（唯一管线构造点）。"
```

---

## Task 12：全量验收

**Files:**
- 无改动（纯验证）

- [ ] **Step 1：全仓构建**

```bash
cd /e/Projects/CSharp/Seeing.Agent
dotnet build Seeing.Agent.slnx
```

Expected: 0 错误（警告数与改动前持平）

- [ ] **Step 2：跑全部受影响测试项目**

```bash
dotnet vstest tests/primitives/Seeing.Markdown.Tests/bin/Debug/net10.0/Seeing.Markdown.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Workbench.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Workbench.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Workbench.Tests/bin/Debug/net10.0/Seeing.Agent.Workbench.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll
```

Expected: 全部 0 失败

- [ ] **Step 3：验收清单逐项核对**

```bash
# ① 管线构造点唯一
rg -n 'MarkdownPipelineBuilder' src samples

# ② 无残留 MD 工作台
rg -n 'MarkdownWorkbench|md-workbench' src samples tests

# ③ 声明表只剩 files
rg -n 'new WorkbenchContribution' src/capabilities/Seeing.Agent.Files

# ④ 无等待型测试
rg -n 'Task\.Delay|Thread\.Sleep' tests/primitives/Seeing.Markdown.Tests tests/apps/Seeing.Agent.WebUI.Files.Tests
```

Expected：
- ① 只在 `src/primitives/Seeing.Markdown/` 内出现
- ② 无输出
- ③ 只有一处 `new WorkbenchContribution`（`files`）
- ④ 无输出

- [ ] **Step 4：浏览器端到端手验**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

逐项确认：

| # | 操作 | 预期 |
|---|---|---|
| 1 | 打开会话页 → 工作台下拉 | 只列「文件」（没有「Markdown 预览」） |
| 2 | 打开 `.md` 文件 | 默认渲染态；表格有框线且可左右滚动 |
| 3 | 点标签栏「编辑」 | 切到 Monaco 源码视图，按钮变「预览」 |
| 4 | 再点「预览」 | 切回渲染态 |
| 5 | 打开 `.cs` 文件 | 无切换按钮，直接 Monaco |
| 6 | 打开超大的 `.md`（>5MB 或被截断） | 走 `FileViewer`，表格仍正常渲染（回归 3 号管线缺陷） |
| 7 | 记忆页 → 任一记忆详情 | 表格正常渲染（回归 4 号管线缺陷） |
| 8 | 让 agent 调 `open_workbench(workbenchId=markdown, payload={path:"README.md"})` | 文件工作台打开且处于预览态 |
| 9 | 让 agent 调 `open_workbench(workbenchId=files, payload={path:"a.md", preview:false})` | 打开且处于编辑态 |
| 10 | agent 上下文里查工作台叙述 | 出现「正在预览 README.md」或「正在编辑 a.md」 |

- [ ] **Step 5：Commit 验收记录（若发现并修复了问题）**

若 Step 3/4 发现问题，修复后单独 commit：

```bash
git add -A
git commit -m "fix(files): <具体问题>

<根因与修复说明>"
```

- [ ] **Step 6：最终确认**

```bash
git status --short
git log --oneline -12
```

Expected: 工作区干净；最近 11 个 commit 对应本计划的 11 个任务。

---

## 附：Task 依赖关系

```
Task 1 (原语包骨架)
  └── Task 2 (管线工厂)
        └── Task 3 (四入口切换)  ← 此时表格缺陷已修复，可独立验证
Task 4 (PreviewMode)
  └── Task 5 (按钮 + 预览分支)
        └── Task 8 (Context.Report)  ← 依赖 Task 5 的分支与 Task 4 的状态
Task 6 (别名解析)  ─┐
Task 7 (删声明)    ─┴── Task 9 (测试同步)
                          └── Task 10 (删组件/CSS)
                                └── Task 11 (文档)
                                      └── Task 12 (验收)
```

Task 1–3 与 Task 4–5 可并行（文件不重叠）；Task 6–7 必须等 Task 5 完成（否则 `preview` 载荷无人消费）。
