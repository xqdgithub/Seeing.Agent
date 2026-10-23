# 内置工具描述与提示词路由优化 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 52 个内置工具的描述与参数 schema 统一为中文规范，并让延迟工具的目录全覆盖、可被完整描述检索，配合内置 Agent 的工具路由段，提升模型选对工具、填对参数的准确率。

**Architecture:** 先建一个跨包的「工具元数据契约测试」项目，用机械规则（中文、首句 ≤60、参数描述齐全、目录行不被截断、兄弟工具互相点名）驱动逐组重写工具描述；再调整目录宽度/预算、把 `tool_search` 的排序语料从截断签名换成完整描述、放宽 summary；最后改写四个内置 Agent 的提示词并加机械校验。描述是主体，引擎改动只限默认值与检索语料，不动分层判定语义。

**Tech Stack:** .NET 10 / C#、xUnit v3 + FluentAssertions + Moq、中央包管理（版本在 Directory.Packages.props）、`dotnet build` + `dotnet vstest`（MTP 在本环境无法发现测试）。

**Spec:** `docs/superpowers/specs/2026-10-23-builtin-tool-descriptions-and-prompt-routing-design.md`

## Global Constraints

- 描述一律简体中文；工具 id、参数名、命令、专有名词保留原文。
- 首句（`Description` 第一行）≤ 60 字符，且前 20 字符内出现「动作 + 对象」。
- 单个工具描述总长 ≤ 700 字符；正文不得出现以 `## ` 开头的行。
- 工具名引用必须用反引号包裹**精确 id**（`read`，不是 `Read`）。
- 路径参数统一表述为「绝对路径，或相对当前工作目录的路径」。
- 不新增 NuGet 包；测试项目沿用现有 `PackageReference` 无版本写法。
- 不改工具行为、执行链、权限判定、暴露分层语义；唯一的行为对齐是 `bash.required` 去掉 `description`、`todowrite.todos` 补 `items`。
- 术语统一：当前会话 / 本次执行 / 下一次执行 / 工作目录 / 子代理；不用「本轮」「下一轮」。
- 提交信息用中文短句，前缀沿用仓库风格（`refactor(tools):`、`feat(tool-exposure):`、`test(tools):`、`docs(architecture):`）。

## Review Focus

- **目录行在长 id + 多参数工具上被截断**：`send_channel_message(channelId, targetUserId, conversationExternalId, conversationKind): ` 会吃掉大部分行宽。期望核心动作词仍然可见 → 由 Task 1 的目录行复算断言钉住。
- **`task` 的运行时描述与 fallback 漂移**：`Description` 在 warm 前后是两份文案。期望两条路径首句一致且都合规 → 由 Task 3 的 warm 测试钉住。
- **新增工具漏登记**：工厂是手写列表，新增工具可能不进契约测试。期望 52 个 id 精确全覆盖 → 由 Task 12 的完整性断言钉住。
- **参数嵌套结构缺描述**：`todos.items`、`systemone.questions[].options[]` 这类深层属性容易被漏。期望每个嵌套属性都有非空 description → 由 Task 1 的递归 schema 断言钉住。
- **MCP 工具被误判**：MCP 描述由 server 提供，不受中文/长度规则约束。期望契约测试只覆盖内置工具 → Task 1 的工厂只登记内置类型。

---

## 文件结构

**创建**

- `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/Seeing.Agent.Tools.Metadata.Tests.csproj`
- `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/BuiltInToolCatalog.cs` — 52 个内置工具的构造工厂（反射自动配参 + SystemOne 四例显式）
- `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/ToolMetadataTests.cs` — 规则断言、路由断言、目录行复算
- `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/TaskDescriptionTests.cs` — `task` 的 fallback / warmed 一致性

**修改（描述）**

- `src/capabilities/Seeing.Agent.Tools.FileSystem/{ReadTool,WriteTool,EditTool,DeleteTool,GrepTool,GlobTool,OpenFileTool,AddWorkspacePathTool}.cs`
- `src/capabilities/Seeing.Agent.Tools.Shell/BashTool.cs`
- `src/hosting/Seeing.Agent.Hosting/Tools/{TaskTool,TaskStatusTool,TodoWriteTool}.cs`
- `src/capabilities/Seeing.Agent.Tools.Session/{SessionListTool,SessionReadTool,SessionSearchTool,SessionTrimTool,SessionHandoffTool,SessionInboxSendTool,SessionTakeoverTool,SessionReleaseTool}.cs`
- `src/capabilities/Seeing.Agent.Memory/Integration/Tools/{MemorySearchTool,MemoryReadTool,MemoryWriteTool}.cs`
- `src/capabilities/Seeing.Agent.Tools.Editor/{EditorQueryTool,EditorListCommandsTool,EditorCommandTool}.cs`
- `src/capabilities/Seeing.Agent.Scheduler/Tools/{CronCreateTool,CronListTool,CronDisableTool,CronResumeTool,CronRunTool,CronDeleteTool}.cs`
- `src/capabilities/Seeing.Agent.Tools.Web/{WebSearchTool,WebFetchTool,CodeSearchTool}.cs`
- `src/capabilities/Seeing.Agent.Skills/SkillTool.cs`
- `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs`
- `src/capabilities/Seeing.Agent.Tools.Git/{GitStatusTool,GitDiffTool,GitLogTool,GitCommitTool}.cs`
- `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/{SendMessageTool,ListConversationsTool,ListChannelUsersTool}.cs`
- `src/capabilities/Seeing.Agent.Acp/Tools/{AcpTool,AcpStatusTool}.cs`
- `src/capabilities/Seeing.Agent.Tools.SystemOne/SystemOneTool.cs`
- `src/capabilities/Seeing.Agent.Tools.Basic/CurrentTimeTool.cs`
- `src/capabilities/Seeing.Agent.Tools.Question/QuestionTool.cs`

**修改（引擎/配置/提示词/测试/文档）**

- `src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs` — `CatalogLineMaxChars`、`CatalogBudgetChars` 默认值
- `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs` — 目录覆盖 + 默认值锁定
- `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/ToolSearchToolTests.cs` — 检索语料、summary 长度、既有断言更新
- `src/capabilities/Seeing.Agent.Agents.BuiltIn/BuiltInAgents.cs` — 四个 Agent 的「## 工具选择」
- `tests/capabilities/Seeing.Agent.Agents.BuiltIn.Tests/PromptRoutingTests.cs`（创建）— 段落存在 + 引用可用性
- `Seeing.Agent.slnx` — 登记新测试项目
- `docs/architecture/04-development-standards.md`、`docs/architecture/19-tool-context-budgeting-release-notes.md`、`AGENTS.md`

---

### Task 1: 契约测试骨架 + 文件系统读写四件套

**Files:**
- Create: `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/Seeing.Agent.Tools.Metadata.Tests.csproj`
- Create: `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/BuiltInToolCatalog.cs`
- Create: `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/ToolMetadataTests.cs`
- Modify: `Seeing.Agent.slnx:95-120`（`/tests/capabilities/` 下追加一行）
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/ReadTool.cs:40-44`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/WriteTool.cs:39-43,52-60`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/EditTool.cs:39-43,56-60`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/DeleteTool.cs:32-34,47-51`

**Interfaces:**
- Produces: `BuiltInToolCatalog.All()` → `IReadOnlyList<ITool>`（后续任务往列表里追加条目）；`ToolMetadataTests` 的 `[Theory]` 数据源 `ToolCases()` → `(string Id, string Description, JsonElement Parameters)`；静态字典 `RoutingExpectations`（`toolId → 必须被点名的兄弟 id`）。
- Consumes: `ITool`（`Seeing.Agent.Abstractions.Tools`）、`SeeingAgentOptions.ToolExposureOptions`（`Seeing.Agent.Core.Configuration`）。

- [ ] **Step 1: 建测试项目并登记到解决方案**

`Seeing.Agent.Tools.Metadata.Tests.csproj`（沿用现有测试项目写法，含 `Microsoft.NET.Test.Sdk`、`xunit.v3`、`xunit.runner.visualstudio`、`coverlet.collector`、`FluentAssertions`、`Moq`），`ProjectReference` 指向：`Seeing.Agent.Abstractions`、`Seeing.Agent.Core`、`Seeing.Agent.Hosting`、`Seeing.Agent.Acp`、`Seeing.Agent.Skills`、`Seeing.Agent.Memory`、`Seeing.Agent.Scheduler`、`Seeing.Agent.Tools.Basic`、`Seeing.Agent.Tools.ChannelMessaging`、`Seeing.Agent.Tools.Editor`、`Seeing.Agent.Tools.FileSystem`、`Seeing.Agent.Tools.Git`、`Seeing.Agent.Tools.Question`、`Seeing.Agent.Tools.Session`、`Seeing.Agent.Tools.Shell`、`Seeing.Agent.Tools.SystemOne`、`Seeing.Agent.Tools.ToolDiscovery`、`Seeing.Agent.Tools.Web`。

在 `Seeing.Agent.slnx` 的 `<Folder Name="/tests/capabilities/">` 内按字母序插入 `<Project Path="tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/Seeing.Agent.Tools.Metadata.Tests.csproj" />`。

- [ ] **Step 2: 写工具工厂（先只含 4 个）**

`BuiltInToolCatalog.cs`：

```csharp
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.FileSystem;

namespace Seeing.Agent.Tools.Metadata.Tests;

internal static class BuiltInToolCatalog
{
    public static IReadOnlyList<ITool> All() =>
    [
        Auto<ReadTool>(), Auto<WriteTool>(), Auto<EditTool>(), Auto<DeleteTool>(),
    ];

    /// <summary>用最少参数的构造函数创建实例；只读取 Description / ParametersSchema，不执行任何工具逻辑。</summary>
    public static ITool Auto<T>() where T : ITool => (ITool)Create(typeof(T));

    private static object Create(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        // 所有内置工具构造函数只做赋值（无 ArgumentNullException 校验），
        // 且测试只读取 Description / ParametersSchema，不会触发依赖调用。
        var args = ctor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        return ctor.Invoke(args);
    }
}
```

> `BashTool.Description` 会读 `_shellService`、`AcpTool.Description` 会读 `_backendRegistry`，两者内部都有 `try/catch`，null 时回退为「未知」/无后端文案，不影响断言。

- [ ] **Step 3: 写规则测试（RED）**

`ToolMetadataTests.cs`：

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tools.Metadata.Tests;

public class ToolMetadataTests
{
    public static TheoryData<string> ToolIds() =>
        new(BuiltInToolCatalog.All().Select(t => t.Id));

    private static readonly Dictionary<string, string[]> RoutingExpectations = new()
    {
        ["read"] = ["open_file"],
        ["write"] = ["edit"],
        ["edit"] = ["read", "write"],
    };

    /// <summary>回退规则：没登记映射的工具按空数组处理。</summary>
    private static IReadOnlyList<string> Routing(string id) =>
        RoutingExpectations.TryGetValue(id, out var v) ? v : [];

    [Theory, MemberData(nameof(ToolIds))]
    public void Description_ShouldFollowConventions(string id)
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        var text = tool.Description;
        var firstLine = text.Split('\n')[0].Trim();

        text.Should().NotBeNullOrWhiteSpace();
        Regex.IsMatch(text, "[\u4e00-\u9fff]").Should().BeTrue($"{id} 描述必须包含中文");
        firstLine.Length.Should().BeLessThanOrEqualTo(60, $"{id} 首句过长：{firstLine}");
        text.Length.Should().BeLessThanOrEqualTo(700);
        text.Split('\n').Should().NotContain(l => l.TrimStart().StartsWith("## "));
    }

    [Theory, MemberData(nameof(ToolIds))]
    public void Description_ShouldNameSiblingTools(string id)
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        foreach (var sibling in Routing(id))
            tool.Description.Should().Contain($"`{sibling}`", $"{id} 必须点名 {sibling}");
    }

    [Theory, MemberData(nameof(ToolIds))]
    public void Parameters_ShouldDescribeEveryProperty(string id)
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        AssertDescribed(tool.ParametersSchema, id, "");
    }

    [Theory, MemberData(nameof(ToolIds))]
    public void CatalogLine_ShouldKeepFirstTwentyChars(string id)
    {
        var options = new SeeingAgentOptions.ToolExposureOptions();
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        var firstLine = tool.Description.Split('\n')[0].Trim();
        var head = firstLine[..Math.Min(20, firstLine.Length)];

        BuildCatalogLine(tool, options.CatalogLineMaxChars).Should().Contain(head);
    }

    private static void AssertDescribed(JsonElement schema, string id, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                prop.Value.ValueKind.Should().Be(JsonValueKind.Object,
                    $"{id} 的属性 {path}{prop.Name} 必须是对象 schema");

                var described =
                    prop.Value.TryGetProperty("description", out var d) &&
                    d.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(d.GetString());
                described.Should().BeTrue($"{id} 的属性 {path}{prop.Name} 缺少非空 description");

                AssertDescribed(prop.Value, id, $"{path}{prop.Name}.");
            }
        }

        if (schema.TryGetProperty("items", out var items))
            AssertDescribed(items, id, $"{path}[]");

        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            var desc = schema.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";
            foreach (var v in values.EnumerateArray())
                desc.Should().Contain(v.GetString()!, $"{id} 的 {path} 描述必须列出枚举值");
        }
    }

    private static string BuildCatalogLine(ITool tool, int lineMax)
    {
        var schema = tool.ParametersSchema;
        var names = new List<string>();
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                foreach (var r in req.EnumerateArray()) required.Add(r.GetString()!);

            foreach (var p in props.EnumerateObject())
            {
                names.Add(required.Contains(p.Name) ? p.Name : p.Name + "?");
                if (names.Count == 4) break;
            }
        }

        var prefix = names.Count == 0 ? $"{tool.Id}: " : $"{tool.Id}({string.Join(", ", names)}): ";
        var desc = Regex.Replace(tool.Description.Replace("\r", ""), @"\s+", " ");
        var line = prefix + desc;
        return line.Length <= lineMax ? line : line[..(lineMax - 1)] + "…";
    }
}
```

Run: `dotnet build tests/capabilities/Seeing.Agent.Tools.Metadata.Tests` then `dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll`
Expected: FAIL — `write` 未点名 `edit`、`edit` 未点名 `read`/`write`、`read` 未点名 `open_file`。

- [ ] **Step 4: 重写四个工具的描述与路径语义**

| 工具 | 首句（精确写入，作为 `Description` 第一行） | 正文必含 | schema 改动 |
|------|------|------|------|
| `read` | `读取文件或目录内容，返回带行号的文本；查看文件原文用此工具。` | 分页 `offset`/`limit`（默认 2000 行）；单行超 2000 字符截断；二进制/图片/PDF 返回附件；目录返回条目列表；不用于把文件呈现给用户（用 `open_file`） | `path` 描述改「绝对路径，或相对当前工作目录的路径」 |
| `write` | `新建文件或整体重写；只改现有文件的一小段请用 \`edit\`。` | 已存在时整文件替换、不保留原内容；父目录自动创建；不用于读取（用 `read`） | `path` 同上 |
| `edit` | `对现有文件做精确字符串替换；改代码片段用此工具，不要整文件覆盖。` | 前置读 `read`；`oldString` 必须唯一，多匹配需更多上下文或 `replaceAll=true`；空白/缩进/行尾严格匹配；失败不写入；`oldString` 为空串才新建（通常直接用 `write`） | `path` 同上；`replaceAll` 补默认 false |
| `delete` | `删除文件或整个目录，递归且不可恢复；仅用于确需移除的路径。` | 目录递归删除含子项；不可恢复 | `path` 同上 |

- [ ] **Step 5: 跑测试确认全绿**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll`
Expected: PASS（4 个 `[Theory]` × 4 条规则）。

- [ ] **Step 6: 提交**

```bash
git add tests/capabilities/Seeing.Agent.Tools.Metadata.Tests Seeing.Agent.slnx src/capabilities/Seeing.Agent.Tools.FileSystem
git commit -m "test(tools): 建立工具元数据契约测试并重写文件读写描述"
```

---

### Task 2: 文件系统搜索与呈现四件套

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`（追加 4 个条目）
- Modify: `tests/.../ToolMetadataTests.cs`（追加路由映射）
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/GrepTool.cs:34-37,46-60`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/GlobTool.cs:40-44,57-66`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/OpenFileTool.cs:36-40,55-58`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/AddWorkspacePathTool.cs:46`

**Interfaces:**
- Consumes: `BuiltInToolCatalog.Auto<T>()`、`RoutingExpectations`（Task 1）。

- [ ] **Step 1: 追加条目与路由期望**

`BuiltInToolCatalog.All()` 追加 `Auto<GrepTool>()`、`Auto<GlobTool>()`、`Auto<OpenFileTool>()`、`Auto<AddWorkspacePathTool>()`。
`RoutingExpectations` 追加：`["glob"] = ["grep"]`、`["grep"] = ["glob"]`、`["open_file"] = ["read"]`。

Run: `dotnet vstest ...Metadata.Tests.dll`
Expected: FAIL — `glob`/`grep`/`open_file` 未点名兄弟工具。

- [ ] **Step 2: 重写描述与 schema**

| 工具 | 首句 | 正文必含 | schema 改动 |
|------|------|------|------|
| `grep` | `按正则搜索文件内容，返回路径与行号；按文件名找文件用 \`glob\`。` | 递归搜索；`include` 支持 `*.{ts,tsx}`；默认最多 100 条；无效正则直接报错；默认搜索工作目录 | `path` 改「绝对路径，或相对当前工作目录的路径（默认工作目录）」；`pattern` 标明是正则 |
| `glob` | `按文件名或路径模式查找文件；按内容搜索用 \`grep\`。` | 支持 `**`；按修改时间倒序；默认最多 100 个；结果被截断时提示 | `pattern` 给示例 `**/*.cs`；`path` 同上 |
| `open_file` | `在用户编辑器中打开文件并定位；只用于呈现，不读取内容（用 \`read\`）。` | 需宿主支持；已打开则切换、当前文件则刷新；`line`/`column` 从 1 开始；无磁盘副作用 | `path` 改「绝对路径，或相对当前工作目录的路径」 |
| `add_workspace_path` | `请求把目录加入当前会话工作区白名单，批准后该目录免询问。` | 仅当前会话生效；需用户批准；已覆盖路径无需重复添加 | `path` 同上 |

- [ ] **Step 3: 跑测试确认全绿**

Run: `dotnet vstest ...Metadata.Tests.dll`
Expected: PASS（8 个工具）。

- [ ] **Step 4: 提交**

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Tools.FileSystem
git commit -m "refactor(tools): 重写文件搜索与编辑器呈现工具描述"
```

---

### Task 3: Shell、子代理与任务清单（含两处 schema 对齐）

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`
- Modify: `tests/.../ToolMetadataTests.cs`
- Create: `tests/.../TaskDescriptionTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Shell/BashTool.cs:31-47`
- Modify: `src/hosting/Seeing.Agent.Hosting/Tools/TaskTool.cs:51-65,441-460`
- Modify: `src/hosting/Seeing.Agent.Hosting/Tools/TaskStatusTool.cs:37-50`
- Modify: `src/hosting/Seeing.Agent.Hosting/Tools/TodoWriteTool.cs:28-55`

**Interfaces:**
- Produces: `TaskDescriptionTests` 断言 `Description`（fallback）与 `WarmDescriptionAsync` 后文本首句一致且合规。
- Consumes: `TaskTool.WarmDescriptionAsync(CancellationToken)`、`IAgentRegistry.GetTaskableAgentsAsync()`（`Seeing.Agent.Abstractions.Agents`）。

- [ ] **Step 1: 追加条目、路由与断言（RED）**

`All()` 追加 `Auto<BashTool>()`、`Auto<TaskTool>()`、`Auto<TaskStatusTool>()`、`Auto<TodoWriteTool>()`。
`RoutingExpectations` 追加：`["bash"] = ["read","grep"]`、`["task"] = ["task_status"]`、`["task_status"] = ["task"]`。

`TaskDescriptionTests.cs`：

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Hosting.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Metadata.Tests;

public class TaskDescriptionTests
{
    [Fact]
    public async Task FallbackAndWarmed_ShouldShareFirstSentence()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetTaskableAgentsAsync())
            .ReturnsAsync(new[]
            {
                new AgentDefinition { Name = "explore", Description = "探索" },
                new AgentDefinition { Name = "general", Description = "通用" },
            });

        var tool = new TaskTool(
            null!,
            null!,
            null!,
            registry.Object,
            null!,
            null!);

        var fallbackFirst = tool.Description.Split('\n')[0].Trim();
        await tool.WarmDescriptionAsync();
        var warmedFirst = tool.Description.Split('\n')[0].Trim();

        warmedFirst.Should().Be(fallbackFirst);
        warmedFirst.Length.Should().BeLessThanOrEqualTo(60);
        tool.Description.Should().Contain("explore").And.Contain("general");
    }
}
```

> `TaskTool` 构造签名（按当前代码，顺序固定）：`(ILogger<TaskTool>, ISessionManager, ISessionGroupManager, IAgentRegistry, ISessionRuntime, IExecutionEventPublisher)`。只有 `IAgentRegistry` 需要真实 mock（`WarmDescriptionAsync` 会调用它），其余传 `null!`。

Run: `dotnet vstest ...Metadata.Tests.dll`
Expected: FAIL — `task` 首句超过 60、`todowrite` 描述无换行导致首句 = 整段。

- [ ] **Step 2: 在规则测试里加两条 schema 断言**

`ToolMetadataTests` 新增：

```csharp
[Fact]
public void Bash_ShouldOnlyRequireCommand()
{
    var bash = BuiltInToolCatalog.All().Single(t => t.Id == "bash");
    var required = bash.ParametersSchema.GetProperty("required")
        .EnumerateArray().Select(e => e.GetString()).ToArray();
    required.Should().Equal("command");
}

[Fact]
public void TodoWrite_ShouldDescribeItemShape()
{
    var tool = BuiltInToolCatalog.All().Single(t => t.Id == "todowrite");
    var items = tool.ParametersSchema.GetProperty("properties").GetProperty("todos").GetProperty("items");
    var props = items.GetProperty("properties");
    props.TryGetProperty("content", out _).Should().BeTrue();
    props.GetProperty("status").GetProperty("enum").GetArrayLength().Should().Be(5);
    props.GetProperty("priority").GetProperty("enum").GetArrayLength().Should().Be(3);
}
```

Run: `dotnet vstest ...Metadata.Tests.dll`
Expected: FAIL — `bash.required` 含 `description`、`todos` 缺 `items`。

- [ ] **Step 3: 实现描述与 schema**

| 工具 | 首句 | 正文必含 | schema 改动 |
|------|------|------|------|
| `bash` | `执行 Shell 命令（构建、测试、脚本）；文件读写搜索优先用专用工具。` | 文件与搜索用 `read`/`write`/`edit`/`grep`/`glob` 而非 shell；超时默认 120000 毫秒；`workdir` 默认工作目录；危险命令会被拒绝；平台与 Shell 语法已在描述中动态给出 | `required` 改为 `["command"]`；`description` 标为可选「命令用途（5–10 字），缺省时自动取命令前 50 字符」；`timeout` 注明默认值与单位；`workdir` 注明默认 |
| `task` | `创建或续跑子代理任务；需要并行推进时放后台执行。` | `subagent_type` 取自下方动态列表；`task_id` 传入即续跑、不传即新建；前台默认、`background=true` 并行；仅 Native 子代理；查看过程用 `session_read`；等待结果用 `task_status` | `subagent_type` 描述指向动态列表；`task_id` 说明不传≠传空串；`run_in_background` 标注为兼容别名 |
| `task_status` | `查询子代理任务状态；需要等结果时用 wait 阻塞等待。` | 不传 `task_id` 返回当前会话全部子任务；`wait=true` 仅对单个任务有效；`timeout_ms` 默认 600000；继续某任务用 `task` 传 `task_id` | 各参数补默认值与单位 |
| `todowrite` | `维护当前会话的结构化任务列表，跟踪多步任务进度。` | 3 步以上用；一次只保留一个 `in_progress`；完成立即标 `completed`；等待用户时标 `paused`；状态枚举含义 | `todos` 改为手写 schema，含 `items`：`content`(必填)、`status`(enum 5)、`priority`(enum 3) |

`TaskTool.BuildDescriptionAsync` 改为「首句复用 `FallbackDescription` 的第一行 + 换行 + 可用子代理列表」，保证 warm 前后首句一致。

- [ ] **Step 4: 跑测试确认全绿**

Run: `dotnet vstest ...Metadata.Tests.dll`
Expected: PASS（12 个工具 + 2 条 schema 断言 + task 一致性）。

- [ ] **Step 5: 提交**

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Tools.Shell src/hosting/Seeing.Agent.Hosting/Tools
git commit -m "refactor(tools): 重写 shell 与子代理工具描述并对齐 bash/todowrite schema"
```

---

### Task 4: 会话协作八件套

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`、`tests/.../ToolMetadataTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Session/*.cs`（8 个文件，描述与 `path`/参数文案）

**Interfaces:**
- Consumes: Task 1 的工厂与规则。

- [ ] **Step 1: 追加条目与路由（RED）**

`All()` 追加 `SessionListTool`、`SessionReadTool`、`SessionSearchTool`、`SessionTrimTool`、`SessionHandoffTool`、`SessionInboxSendTool`、`SessionTakeoverTool`、`SessionReleaseTool`（均用 `Auto<T>()`）。

`RoutingExpectations` 追加：`session_list → [session_read]`、`session_read → [session_search]`、`session_search → [session_read]`、`session_trim → [session_handoff]`、`session_handoff → [session_trim]`、`session_takeover → [session_release]`、`session_release → [session_takeover]`。

Run: `dotnet vstest ...Metadata.Tests.dll`
Expected: FAIL（`session_read` 首句超 60、兄弟工具未点名）。

- [ ] **Step 2: 重写描述**

| 工具 | 首句 | 正文必含 |
|------|------|------|
| `session_list` | `列出当前会话组内的会话；同分区全部会话用 scope=all（需审批）。` | 默认 30 条、上限 100；`offset` 分页；`all` 需审批；查看某个会话的消息用 `session_read` |
| `session_read` | `读取会话消息（按 id、区间或分页）；查看子代理过程用它。` | 五种读法（from/to/闭区间/中心窗口/分页）；默认 20 条、上限 50；`max_chars` 默认 2000；默认隐藏工具输出与已压缩历史；不返回思考内容；只找关键词用 `session_search` |
| `session_search` | `按正则搜索会话消息；只找关键词，不要整段翻页用 \`session_read\`。` | 缺省当前会话；跨会话需审批；`role` 过滤；上下文行上限 3；默认 30、上限 100 |
| `session_trim` | `裁剪当前会话早期消息并自动备份；不会切换会话。` | `mode` 与 id 组合；最后一条摘要、最后一条 user 消息及之后永不裁剪；备份失败则取消；需要换会话用 `session_handoff` |
| `session_handoff` | `把当前会话交接给新会话继续；上下文过长时用它保持精简。` | 创建 Clean Root 后继、继承工作目录/模型；`prompt` 作为首条用户消息并提交；需审批；失败回滚；只想裁剪不换会话用 `session_trim` |
| `session_inbox_send` | `向目标会话投递一条人类身份消息；需要驱动消费时用 wake。` | `wake` 落盘并激活、`inject` 仅落盘；同组+父链默认范围、跨组需审批 |
| `session_takeover` | `接管目标会话（归属改为人类），父代理不再驱动；只改归属。` | 不终止正在运行的执行；交回用 `session_release`；跨组需审批 |
| `session_release` | `交还目标会话（归属改回父会话）以便代理继续驱动；只改归属。` | 不触发立即执行；接管用 `session_takeover`；跨组需审批 |

- [ ] **Step 3: 跑测试确认全绿并提交**

Run: `dotnet vstest ...Metadata.Tests.dll` → PASS（20 个工具）

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Tools.Session
git commit -m "refactor(tools): 重写会话协作工具描述并补兄弟工具路由"
```

---

### Task 5: 记忆三件套 + 编辑器三件套

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`、`tests/.../ToolMetadataTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Integration/Tools/{MemorySearchTool,MemoryReadTool,MemoryWriteTool}.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Editor/{EditorQueryTool,EditorListCommandsTool,EditorCommandTool}.cs`

- [ ] **Step 1: 追加条目与路由（RED）**

`All()` 追加 6 个 `Auto<T>()`。
`RoutingExpectations` 追加：`memory_search → [memory_read]`、`memory_read → [memory_search]`、`editor_query → [open_file]`、`editor_list_commands → [editor_command]`、`editor_command → [editor_list_commands]`。

Run: `dotnet vstest ...Metadata.Tests.dll` → FAIL。

- [ ] **Step 2: 重写描述**

| 工具 | 首句 | 正文必含 |
|------|------|------|
| `memory_search` | `搜索长期记忆（daily、digest），返回相对路径与内容摘要。` | 返回条数由记忆配置 `TopK`（默认 5）决定；取全文用 `memory_read`；不要用文件系统 `read` |
| `memory_read` | `读取一条长期记忆全文；path 取 \`memory_search\` 返回的相对路径。` | `path` 为 memory 相对路径（如 `daily/2026-07-18/xxx.md`）；不要猜测绝对路径；不要用 `read` |
| `memory_write` | `把用户偏好、决策与关键事实写入长期记忆，立即持久化可检索。` | 用于用户明确要求记住或关键决策；`tags` 可选；写入即生效 |
| `editor_query` | `读取用户 Monaco 编辑器的选区、光标、诊断或内容（只读）。` | `kind` 五个取值含义；`path` 可选；需 WebUI 打开文件面板，未打开时可先用 `open_file` 打开目标文件；读磁盘文件用 `read` |
| `editor_list_commands` | `列出用户 Monaco 编辑器支持的命令 id；不确定时先查。` | 需 WebUI 文件面板；返回 id/label；确认后用 `editor_command` 执行 |
| `editor_command` | `在用户 Monaco 编辑器中执行命令；改完不会自动保存。` | `commandId` 示例（`editor.action.formatDocument`、`editor.foldAll`）；`args`/`path` 可选；不确定 `commandId` 时先用 `editor_list_commands`；需用户手动保存；落盘修改用 `edit` |

- [ ] **Step 3: 跑测试确认全绿并提交**

Run: `dotnet vstest ...Metadata.Tests.dll` → PASS（26 个工具）

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Memory src/capabilities/Seeing.Agent.Tools.Editor
git commit -m "refactor(tools): 重写记忆与编辑器工具描述"
```

---

### Task 6: 定时任务六件套

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`、`tests/.../ToolMetadataTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Tools/*.cs`（6 个文件）

- [ ] **Step 1: 追加条目与路由（RED）**

`All()` 追加 6 个 `Auto<T>()`。
`RoutingExpectations` 追加：`cron_disable → [cron_resume]`、`cron_resume → [cron_disable]`、`cron_delete → [cron_disable]`。

Run: `dotnet vstest ...Metadata.Tests.dll` → FAIL（`cron_create` 首句超 60）。

- [ ] **Step 2: 重写描述**

| 工具 | 首句 | 正文必含 |
|------|------|------|
| `cron_create` | `创建或替换定时任务（cron、interval、once）；会话取自当前会话。` | `taskType`：`text` 投递文案 / `agent` 运行 Agent；`schedule` 结构（`cron`/`every`/`runAt`/`timezone`/`windows`）；`windows` 仅 interval、允许跨午夜、禁止交叠；`model` 仅 agent（Native 用 modelId、ACP 用 provider/model）；`mode` 仅 ACP |
| `cron_list` | `列出全部定时任务及下次触发时间；无需参数。` | 返回 intent 与下次触发 |
| `cron_disable` | `禁用定时任务并从调度中移除；之后可用 cron_resume 恢复。` | 不可删除；恢复用 `cron_resume` |
| `cron_resume` | `重新启用已禁用的定时任务。` | 恢复为 Active；禁用用 `cron_disable` |
| `cron_run` | `立即执行一次指定定时任务，不影响后续调度。` | 一次性触发；不改变计划 |
| `cron_delete` | `删除定时任务且不可恢复；只想暂停请用 cron_disable。` | 不可恢复；暂停用 `cron_disable` |

- [ ] **Step 3: 跑测试确认全绿并提交**

Run: `dotnet vstest ...Metadata.Tests.dll` → PASS（32 个工具）

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Scheduler
git commit -m "refactor(tools): 重写定时任务工具描述"
```

---

### Task 7: Web 三件套 + 技能 + 工具检索

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`、`tests/.../ToolMetadataTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Web/{WebSearchTool,WebFetchTool,CodeSearchTool}.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/SkillTool.cs:46-51,66`
- Modify: `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs:49-54`（仅描述，不改逻辑）

- [ ] **Step 1: 追加条目与路由（RED）**

`All()` 追加 `WebSearchTool`、`WebFetchTool`、`CodeSearchTool`、`SkillTool`、`ToolSearchTool`（`Auto<T>()`；`ToolSearchTool` 两个参数均为接口，反射可 mock）。

`RoutingExpectations` 追加：`websearch → [webfetch]`、`webfetch → [websearch]`、`codesearch → [websearch]`。

Run: `dotnet vstest ...Metadata.Tests.dll` → FAIL（`skill` 全英文、`tool_search` 首句超 60）。

- [ ] **Step 2: 重写描述（含 skill 英译中）**

| 工具 | 首句 | 正文必含 |
|------|------|------|
| `websearch` | `联网搜索网页与新闻，返回多来源结果。` | 默认 8 条；`livecrawl`/`searchType` 取值；已知 URL 用 `webfetch`；找代码示例用 `codesearch` |
| `webfetch` | `抓取指定 URL 的内容；已知地址用它，未知地址用 websearch。` | `format` 默认 markdown；`timeout` 秒、最大 120；仅 http/https；内网地址会被拒 |
| `codesearch` | `搜索代码示例、API 文档与库用法；找网页新闻用 websearch。` | 适合查库/框架用法与最佳实践；示例 query |
| `skill` | `按名称加载技能全文；技能名见系统提示词 Skills 节。` | 技能名取自系统提示词 `## Skills` 节；加载后按其中的工作流执行；不用于一般知识问答 |
| `tool_search` | `检索未内联的工具目录；需要的工具不在 tools 中时先用它。` | `detail` 三档（name/summary/full）；`full` 命中后本次执行内可直接调用、下一次执行起自动内联，每页最多 3 条；`limit` 默认/上限；`offset` 分页 |

- [ ] **Step 3: 跑测试确认全绿并提交**

Run: `dotnet vstest ...Metadata.Tests.dll` → PASS（37 个工具）

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Tools.Web src/capabilities/Seeing.Agent.Skills src/capabilities/Seeing.Agent.Tools.ToolDiscovery
git commit -m "refactor(tools): 重写 web、技能与工具检索描述（含英译中）"
```

---

### Task 8: Git 四件套（全英文改中文）

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`、`tests/.../ToolMetadataTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Git/{GitStatusTool,GitDiffTool,GitLogTool,GitCommitTool}.cs`

- [ ] **Step 1: 追加条目与路由（RED）**

`All()` 追加 4 个 `Auto<T>()`。
`RoutingExpectations` 追加：`git_status → [git_diff]`、`git_diff → [git_status]`、`git_commit → [git_status, git_diff]`。

Run: `dotnet vstest ...Metadata.Tests.dll` → FAIL（4 个描述无中文）。

- [ ] **Step 2: 重写描述与参数（英译中）**

| 工具 | 首句 | 正文必含 | schema 改动 |
|------|------|------|------|
| `git_status` | `查看 Git 仓库状态（分支、领先落后、变更文件）。` | 返回分支、是否干净、变更列表（含暂存标记）；看具体差异用 `git_diff` | `path`：「仓库或子目录路径，默认工作目录」 |
| `git_diff` | `查看 Git 工作区或暂存区的差异。` | 返回统一 diff 文本；先看状态用 `git_status` | 现有参数补中文描述 |
| `git_log` | `查看 Git 提交历史。` | 返回提交记录；需要变更内容用 `git_diff` | 现有参数补中文描述 |
| `git_commit` | `暂存并提交 Git 变更；提交前先用 git_status 与 git_diff 确认。` | `files` 指定暂存文件、缺省按 `all` 行为；`all=true` 暂存全部变更；提交失败返回 stderr | `message`/`files`/`all` 全部中文化并写默认 |

- [ ] **Step 3: 跑测试确认全绿并提交**

Run: `dotnet vstest ...Metadata.Tests.dll` → PASS（41 个工具）

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Tools.Git
git commit -m "refactor(tools): Git 工具描述与参数中文化"
```

---

### Task 9: 通道、ACP、SystemOne、时间与问答（11 个）

**Files:**
- Modify: `tests/.../BuiltInToolCatalog.cs`、`tests/.../ToolMetadataTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/{SendMessageTool,ListConversationsTool,ListChannelUsersTool}.cs`
- Modify: `src/capabilities/Seeing.Agent.Acp/Tools/{AcpTool,AcpStatusTool}.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.SystemOne/SystemOneTool.cs:39-60`
- Modify: `src/capabilities/Seeing.Agent.Tools.Basic/CurrentTimeTool.cs:24-26`
- Modify: `src/capabilities/Seeing.Agent.Tools.Question/QuestionTool.cs:34-41`

- [ ] **Step 1: 追加条目与路由（RED）**

`All()` 追加 11 个条目：`Auto<SendMessageTool>()`、`Auto<ListConversationsTool>()`、`Auto<ListChannelUsersTool>()`、`Auto<AcpTool>()`、`Auto<AcpStatusTool>()`、`new SystemOneTool(SystemOneToolKind.Ask, null!)` 及 Noul/Choice/Score 三例、`Auto<CurrentTimeTool>()`、`Auto<QuestionTool>()`。

`RoutingExpectations` 追加：`send_channel_message → [list_channel_conversations, list_channel_users]`、`list_channel_conversations → [send_channel_message]`、`list_channel_users → [send_channel_message]`、`acp → [task]`、`acp_status → [task_status]`、`systemone_ask → [systemone_noul, systemone_choice, systemone_score]`、`systemone_noul/choice/score → [systemone_ask]`。

Run: `dotnet vstest ...Metadata.Tests.dll` → FAIL。

- [ ] **Step 2: 重写描述**

| 工具 | 首句 | 正文必含 |
|------|------|------|
| `send_channel_message` | `向已登记通道的用户或对话发送文本消息。` | `targetUserId` 与 `conversationExternalId`+`conversationKind` 二选一；`channelId` 如 qq/wecom；先查目录用 `list_channel_conversations`/`list_channel_users` |
| `list_channel_conversations` | `分页列出通道目录中的对话。` | `skip` 默认 0、`take` 默认 50；发送用 `send_channel_message` |
| `list_channel_users` | `分页列出通道目录中的用户。` | 同上 |
| `acp` | `把任务委派给已注册的 ACP 后端代理；Native 子代理用 task。` | `backend` 取已注册 ACP 后端；`task_id` 续跑；`run_in_background` 后台；`cwd` 默认工作目录；后台状态用 `acp_status` |
| `acp_status` | `查询 ACP 后台任务状态；Native 任务用 task_status。` | 传 `task_id` 查单个；不传列全部；等待用 `wait` |
| `systemone_ask` | `用 SystemOne 对同一 state 做混合题型判别（是否、选择、打分）。` | 每项带 `type`（noul/choice/score）；只有一种题型时优先单一题型工具；长链推理用主模型 |
| `systemone_noul` | `用 SystemOne 对 state 做是或否的概率判别。` | `instructions` 可带 `criteriaTrue`/`criteriaFalse`；不要带 `type`/`options`/`levels`；可多题并行；混合题型用 `systemone_ask` |
| `systemone_choice` | `用 SystemOne 在多个选项中判别一个。` | `options` 2–50；不要带 `type`/`levels`；混合题型用 `systemone_ask` |
| `systemone_score` | `用 SystemOne 对 state 做有序分级打分。` | `levels` 2–10，低→高；不要带 `type`/`options`；混合题型用 `systemone_ask` |
| `current_time` | `获取当前时间，可指定 IANA 时区。` | 时区缺省用本机；返回 ISO 8601 含偏移；示例 `Asia/Shanghai`、`UTC` |
| `question` | `需要用户选择、确认或补充信息时用它提问，而不是在正文追问。` | `kind`：single/multiple/text；`single` 默认允许「其他」；`text` 不得带 `options`；`required`/默认值；无交互环境会降级 |

- [ ] **Step 3: 跑测试确认全绿并提交**

Run: `dotnet vstest ...Metadata.Tests.dll` → PASS（52 个工具）

```bash
git add -A tests/capabilities/Seeing.Agent.Tools.Metadata.Tests src/capabilities/Seeing.Agent.Tools.ChannelMessaging src/capabilities/Seeing.Agent.Acp src/capabilities/Seeing.Agent.Tools.SystemOne src/capabilities/Seeing.Agent.Tools.Basic src/capabilities/Seeing.Agent.Tools.Question
git commit -m "refactor(tools): 重写通道、ACP、SystemOne、时间与问答工具描述"
```

---

### Task 10: 目录宽度/预算 + 检索语料 + summary

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs:198-202`
- Modify: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs:49-54,187-207,250-298`
- Modify: `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/ToolSearchToolTests.cs`

**Interfaces:**
- Produces: `ToolSearchTool.SummaryMaxChars = 300`（private const）；`ToolSearchTool.BuildSummaryLine(ExposedTool) → string`；`ToolSearchTool.BuildCorpus(ExposedTool) → string`。

- [ ] **Step 1: 写失败的测试**

`ToolExposureEngineTests` 新增（`using Seeing.Agent.Core.Configuration;`，`using Seeing.Agent.Abstractions.Tools;` 已有）：

```csharp
[Fact]
public void DefaultCatalogBudget_ShouldShowEveryDeferredTool()
{
    var desc = new string('描', 300);
    var tools = Enumerable.Range(0, 35)
        .Select(i => Schema($"tool_{i:00}", desc: desc))
        .Append(Schema("tool_search"))
        .ToArray();
    var options = new SeeingAgentOptions.ToolExposureOptions();
    var config = new EffectiveExposureConfig(
        ToolExposurePolicyMode.Tiered,
        Array.Empty<string>(),
        options.CatalogBudgetChars,
        options.CatalogLineMaxChars,
        9999,
        999999);

    var result = ToolExposureEngine.Compute(Input(tools, config), Counter);

    result.CatalogText.Should().NotBeNull();
    for (var i = 0; i < 35; i++)
        result.CatalogText!.Should().Contain($"tool_{i:00}(");
    result.CatalogText!.Length.Should().BeLessThanOrEqualTo(options.CatalogBudgetChars);
}

[Fact]
public void ExposureDefaults_ShouldMatchDesignedValues()
{
    var options = new SeeingAgentOptions.ToolExposureOptions();
    options.CatalogLineMaxChars.Should().Be(200);
    options.CatalogBudgetChars.Should().Be(8000);
}
```

`ToolSearchToolTests` 新增：

```csharp
[Fact]
public async Task Summary_ShouldUseDescriptionBeyondSignatureTruncation()
{
    var desc = "开头" + new string('中', 240) + "尾部关键词甲";
    var tool = CreateTool(Entry("deep_tool", signature: "deep_tool(path)", summary: desc));

    var result = await tool.ExecuteAsync(Args(new { }), _context);

    result.Output.Should().Contain("尾部关键词甲");
}

[Fact]
public async Task Search_ShouldMatchKeywordFromDescriptionTail()
{
    var desc = "开头说明" + new string('中', 240) + "尾部关键词甲";
    var tool = CreateTool(Entry("deep_tool", signature: "deep_tool(path)", summary: desc));

    var result = await tool.ExecuteAsync(Args(new { query = "尾部关键词甲" }), _context);

    result.Output.Should().Contain("deep_tool");
    GetMetaInt(result, "total").Should().Be(1);
}
```

`Entry` 需要让 `Schema.Function.Description` 用到 `summary`（当前实现已如此），无需改夹具。

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests && dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolExposureEngineTests"`
Expected: FAIL（35 条放不进 2000；默认值仍是 120/2000）。

- [ ] **Step 2: 改默认值**

`SeeingAgentOptions.cs`：`CatalogBudgetChars = 8000`、`CatalogLineMaxChars = 200`（注释写明「35 个延迟工具全量可见」的依据）。

Run: 上面命令 → PASS。

- [ ] **Step 3: 改检索语料与 summary**

`ToolSearchTool`：

```csharp
private const int SummaryMaxChars = 300;

private static string BuildSummaryLine(ExposedTool entry)
{
    var fn = entry.Schema.Function;
    var desc = NormalizeWhitespace(fn?.Description);
    if (desc.Length > SummaryMaxChars)
        desc = desc[..(SummaryMaxChars - 1)] + "…";
    var parameters = DescribeParameters(fn?.Parameters);
    return string.IsNullOrEmpty(parameters)
        ? $"{entry.Id}: {desc}"
        : $"{entry.Id}({parameters}): {desc}";
}

private static string BuildCorpus(ExposedTool entry)
{
    var fn = entry.Schema.Function;
    var sb = new StringBuilder()
        .Append(entry.Id).Append(' ')
        .Append(entry.Namespace ?? string.Empty).Append(' ')
        .Append(fn?.Description ?? string.Empty).Append(' ');
    if (fn?.Parameters is { ValueKind: JsonValueKind.Object } p
        && p.TryGetProperty("properties", out var props)
        && props.ValueKind == JsonValueKind.Object)
    {
        foreach (var prop in props.EnumerateObject())
            sb.Append(prop.Name).Append(' ');
    }
    return sb.ToString().ToLowerInvariant();
}
```

`Rank` 内把 `var summaryLower = entry.Signature.ToLowerInvariant();` 与 `fullText` 改为基于 `BuildCorpus(entry)`（语料已小写，避免重复 `ToLowerInvariant`）；分档打分保持不变。

输出循环：`detail == "summary"` 时用 `BuildSummaryLine(entry)` 替代 `entry.Signature`；`detail == "full"` 保持原样（先签名再完整 schema）。

`DescribeParameters` / `NormalizeWhitespace` 在本类新增私有实现（ToolDiscovery 不能引用 Hosting 的内部实现）：

```csharp
private static string DescribeParameters(JsonElement? parameters)
{
    if (parameters is not { ValueKind: JsonValueKind.Object } root
        || !root.TryGetProperty("properties", out var properties)
        || properties.ValueKind != JsonValueKind.Object)
        return string.Empty;

    var required = new HashSet<string>(StringComparer.Ordinal);
    if (root.TryGetProperty("required", out var requiredNode) && requiredNode.ValueKind == JsonValueKind.Array)
        foreach (var item in requiredNode.EnumerateArray())
            if (item.GetString() is { } name) required.Add(name);

    var names = new List<string>();
    foreach (var property in properties.EnumerateObject())
    {
        names.Add(required.Contains(property.Name) ? property.Name : property.Name + "?");
        if (names.Count == 4) break;
    }
    return names.Count == 4 ? string.Join(", ", names) + ", …" : string.Join(", ", names);
}

private static string NormalizeWhitespace(string? text)
{
    if (string.IsNullOrEmpty(text)) return string.Empty;
    var builder = new StringBuilder(text.Length);
    var pendingSpace = false;
    foreach (var ch in text)
    {
        if (char.IsWhiteSpace(ch)) { pendingSpace = builder.Length > 0; continue; }
        if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
        builder.Append(ch);
    }
    return builder.ToString();
}
```

- [ ] **Step 4: 更新既有 summary 断言**

`ToolSearchToolTests.DetailSummary_ShouldIncludeSignatureAndSummary` 现在得到 `file_read: 读文件`（夹具 schema 无参数）。改为断言 `file_read` 与 `读文件`，删除 `read(path)` 断言；测试名改为 `DetailSummary_ShouldIncludeIdAndDescription`。

Run: `dotnet vstest ...ToolDiscovery.Tests.dll`
Expected: PASS（含两条新用例）。

- [ ] **Step 5: 提交**

```bash
git add -A src/spine/Seeing.Agent.Core/Configuration src/capabilities/Seeing.Agent.Tools.ToolDiscovery tests/hosting/Seeing.Agent.Hosting.Tests tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests
git commit -m "feat(tool-exposure): 目录覆盖全部延迟工具，检索改用完整描述"
```

---

### Task 11: 内置 Agent 的工具路由段

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Agents.BuiltIn/BuiltInAgents.cs`（build 58-76、plan 133-174、explore 244-259、general 312-334）
- Create: `tests/capabilities/Seeing.Agent.Agents.BuiltIn.Tests/PromptRoutingTests.cs`

**Interfaces:**
- Produces: 四个 Agent 的 `SystemPrompt` 均含 `## 工具选择`；测试辅助 `ExtractSection(string prompt, string heading)`。

- [ ] **Step 1: 写失败的测试**

```csharp
using System.Text.RegularExpressions;
using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Xunit;

namespace Seeing.Agent.Agents.BuiltIn.Tests;

public class PromptRoutingTests
{
    private static readonly string[] s_allToolIds =
    [
        "read","write","edit","delete","grep","glob","open_file","add_workspace_path","bash",
        "task","task_status","todowrite",
        "session_list","session_read","session_search","session_trim","session_handoff",
        "session_inbox_send","session_takeover","session_release",
        "memory_search","memory_read","memory_write",
        "editor_query","editor_list_commands","editor_command",
        "cron_create","cron_list","cron_disable","cron_resume","cron_run","cron_delete",
        "websearch","webfetch","codesearch","skill","tool_search",
        "git_status","git_diff","git_log","git_commit",
        "send_channel_message","list_channel_conversations","list_channel_users",
        "acp","acp_status",
        "systemone_ask","systemone_noul","systemone_choice","systemone_score",
        "current_time","question"
    ];

    [Theory]
    [InlineData("build")]
    [InlineData("plan")]
    [InlineData("explore")]
    [InlineData("general")]
    public void Agent_ShouldHaveToolSelectionSection(string name)
    {
        var prompt = BuiltInAgents.GetBuiltInAgents().Single(a => a.Name == name).SystemPrompt;

        prompt.Should().Contain("## 工具选择");
        prompt.Should().NotContain("## Tools");
        prompt.Should().NotContain("## 工具使用");
    }

    [Theory]
    [InlineData("build")]
    [InlineData("plan")]
    [InlineData("explore")]
    [InlineData("general")]
    public void RoutingSection_ShouldOnlyReferenceAvailableTools(string name)
    {
        var agent = BuiltInAgents.GetBuiltInAgents().Single(a => a.Name == name);
        var section = ExtractSection(agent.SystemPrompt, "## 工具选择");
        var available = AvailableTools(agent);

        foreach (Match m in Regex.Matches(section, "`([^`]+)`"))
        {
            var token = m.Groups[1].Value;
            if (token.Contains('.') || token.Contains('/') || token.Contains('=')) continue;
            if (!s_allToolIds.Contains(token)) continue; // Agent 名等非工具 token
            available.Should().Contain(token, $"{name} 的路由段提到了不可用工具 {token}");
        }
    }

    private static HashSet<string> AvailableTools(Seeing.Agent.Abstractions.Agents.AgentDefinition agent)
    {
        if (agent.AllowedTools.Count > 0) return [.. agent.AllowedTools];
        var allowed = agent.PermissionRules
            .Where(r => r.Kind == PermissionKind.Tool && r.Effect == PermissionEffect.Allow)
            .Select(r => r.Pattern)
            .Where(p => p != "*");
        var set = new HashSet<string>(allowed);
        if (set.Count == 0) set = [.. s_allToolIds];
        foreach (var denied in agent.DeniedTools) set.Remove(denied);
        return set;
    }

    private static string ExtractSection(string prompt, string heading)
    {
        var lines = prompt.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == heading);
        if (start < 0) return string.Empty;
        var end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("## ")) end++;
        return string.Join('\n', lines[start..end]);
    }
}
```

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Agents.BuiltIn.Tests/bin/Debug/net10.0/Seeing.Agent.Agents.BuiltIn.Tests.dll`
Expected: FAIL（无 `## 工具选择`）。

- [ ] **Step 2: 改写四个提示词**

`build`：把 `## 工具使用` 段替换为：

```
## 工具选择
- 专用工具优先于 `bash`：读写文件用 `read`/`write`/`edit`/`delete`，找文件用 `glob`，搜内容用 `grep`。
- 探索代码库按 `glob` 定位、`grep` 找内容、`read` 读细节推进；互不依赖的调用一次并行发出。
- 修改现有文件用 `edit`；`write` 只用于新建或整体重写。
- 委派子代理用 `task`；查看子代理过程用 `session_read`，等待结果用 `task_status`。
- 上下文过长或用户转向新话题时用 `session_handoff`；需要精简当前会话用 `session_trim`。
- 需要用户选择或确认时用 `question`，不要在回复正文里追问。
- 需要的工具不在 `tools` 里时，先用 `tool_search` 检索。
```

`plan`：在「## 职责」之前插入：

```
## 工具选择
- 只读工具：`read`、`grep`、`glob`、`open_file`、`webfetch`、`websearch`、`codesearch`；命令执行与代码编辑在本模式被禁用。
- 探索按 `glob` 定位、`grep` 找内容、`read` 读细节推进；互不依赖的调用并行发出。
- 计划文档写入 `.plans/` 下的 .md 文件，不改动其他文件。
- 需要外部资料用 `webfetch`/`websearch`/`codesearch`；需要用户确认用 `question`。
- 需要子代理探索时用 `task` 委托 explore。
- 需要加载技能时用 `skill`；用 `todowrite` 跟踪计划步骤。
```

`explore`：把 `## 搜索策略` 段替换为：

```
## 工具选择
- 按 `glob` 定位文件、`grep` 搜索内容、`read` 读取细节，从广到窄推进；互不依赖的调用并行发出。
- 只用只读工具，不修改文件、不执行命令。
- 需要仓库变更上下文时用 `git_status`/`git_diff`/`git_log`；需要历史决策时用 `memory_search`/`memory_read`。
- 用 `todowrite` 记录多步探索进度。
```

`general`：把「## 工具使用」段替换为：

```
## 工具选择
- 专用工具优先于 `bash`：读写文件用 `read`/`write`/`edit`，找文件用 `glob`，搜内容用 `grep`。
- 独立操作并行发出；复杂任务先拆步骤，用 `todowrite` 跟踪。
- 需要用户选择或确认时用 `question`；需要的工具不在 `tools` 里时先用 `tool_search` 检索。
- 本代理不支持再委派子代理。
```

- [ ] **Step 3: 跑测试确认全绿**

Run: `dotnet vstest ...Agents.BuiltIn.Tests.dll`
Expected: PASS（8 条用例）。

- [ ] **Step 4: 提交**

```bash
git add src/capabilities/Seeing.Agent.Agents.BuiltIn tests/capabilities/Seeing.Agent.Agents.BuiltIn.Tests
git commit -m "feat(agents): 内置 Agent 增加工具选择路由段"
```

---

### Task 12: 完整性锁定 + 文档 + 全量验证

**Files:**
- Modify: `tests/.../ToolMetadataTests.cs`
- Modify: `docs/architecture/04-development-standards.md`
- Modify: `docs/architecture/19-tool-context-budgeting-release-notes.md:31-32,79`
- Modify: `AGENTS.md`（WHERE TO LOOK 表）

- [ ] **Step 1: 加完整性断言**

`ToolMetadataTests` 新增：

```csharp
[Fact]
public void Catalog_ShouldCoverEveryBuiltInToolExactly()
{
    var ids = BuiltInToolCatalog.All().Select(t => t.Id).ToArray();

    ids.Should().HaveCount(52);
    ids.Should().OnlyHaveUniqueItems();
    ids.Should().BeEquivalentTo(new[]
    {
        "read","write","edit","delete","grep","glob","open_file","add_workspace_path","bash",
        "task","task_status","todowrite",
        "session_list","session_read","session_search","session_trim","session_handoff",
        "session_inbox_send","session_takeover","session_release",
        "memory_search","memory_read","memory_write",
        "editor_query","editor_list_commands","editor_command",
        "cron_create","cron_list","cron_disable","cron_resume","cron_run","cron_delete",
        "websearch","webfetch","codesearch","skill","tool_search",
        "git_status","git_diff","git_log","git_commit",
        "send_channel_message","list_channel_conversations","list_channel_users",
        "acp","acp_status",
        "systemone_ask","systemone_noul","systemone_choice","systemone_score",
        "current_time","question"
    });
}
```

Run: `dotnet vstest ...Metadata.Tests.dll`
Expected: PASS（若之前漏登记，这里会指出缺失 id）。

- [ ] **Step 2: 更新架构文档**

- `docs/architecture/04-development-standards.md`：新增「工具描述与参数 Schema 规范（R1–R9）」小节，内容取自 spec §4–§5（首句 ≤60 且前端加载、正文 ≤700、不用 `##`、精确 id 反引号、路径语义、参数默认值/枚举/边界、`required` 与实现一致），并链接 spec。
- `docs/architecture/19-tool-context-budgeting-release-notes.md`：默认值表改为 `CatalogLineMaxChars = 200`、`CatalogBudgetChars = 8000`（注明 35 个延迟工具全量可见的依据）；补充“展示签名仍截断，检索语料用完整描述 + 参数名”“`detail=summary` 描述段上限 300 字符”；回退开关仍是 `Tools.Exposure.PolicyMode = "All"`。
- `AGENTS.md` WHERE TO LOOK 增加一行：`新增/修改工具描述 | docs/architecture/04-development-standards.md | 描述与参数 Schema 规范（R1–R9）`。

- [ ] **Step 3: 全量构建与测试**

```bash
dotnet build Seeing.Agent.slnx
dotnet build tests/capabilities/Seeing.Agent.Tools.Metadata.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Agents.BuiltIn.Tests/bin/Debug/net10.0/Seeing.Agent.Agents.BuiltIn.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.ToolDiscovery.Tests.dll
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
```

Expected: 构建 0 error；上述测试全绿。仓库既有基线失败（`InstructionManagerTests.InjectIfNeededAsync_FirstDiscovery` 依赖真实 `~/.agents/AGENTS.md`）与本变更无关，记录即可。

- [ ] **Step 4: 记录 token 实测**

从任一现成宿主跑一轮会话，读取 `SchemaSnapshotEvent` 的 `SourceBreakdown.ToolDefinitions` 与 `Catalog`（或会话 `ToolExposure.SchemaTokens`/`CatalogTokens`），把数值写进 PR 描述。不设门槛，只报告。

- [ ] **Step 5: 提交**

```bash
git add -A docs/architecture AGENTS.md tests/capabilities/Seeing.Agent.Tools.Metadata.Tests
git commit -m "docs(architecture): 固化工具描述规范与目录默认值"
```

---

## 自检记录

- **Spec 覆盖**：§4 规范 → Task 1–9（逐组重写 + Task 1 规则）；§5 schema → Task 1–9 的 schema 列 + Task 3 两条断言；§6 清单 52 个 → Task 1–9 全部登记，Task 12 精确校验；§7 提示词 → Task 11；§8.1/8.1.1 → Task 1/3/12；§8.2 → Task 11；§8.3 → Task 10；§9 文档 → Task 12；§12.1–12.3 → Task 10；§12.4 → Task 12 Step 4。
- **命名一致**：`BuiltInToolCatalog.All()`、`RoutingExpectations`（spec 未命名，此处首次定义并在后续任务引用）、`SummaryMaxChars`、`BuildSummaryLine`、`BuildCorpus` 全文一致。
- **Review Focus 对应**：长参数目录行 → Task 1 `CatalogLine_ShouldKeepFirstTwentyChars`；task warm → Task 3；漏登记 → Task 12；嵌套参数 → Task 1 `Parameters_ShouldDescribeEveryProperty`；MCP 不误判 → 工厂只登记内置类型（无 MCP 条目）。
