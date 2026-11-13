# 用户级钩子目录 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `.js` 或 `.dll` 放进用户级 `~/.seeing/hooks` 后，由现有 `IHookManager` 在下一次钩子触发时执行，宿主不用改调用，也不用 `AddSeeingModule`。

**Architecture:** 新程序集 `Seeing.Agent.Hooks.Directory` 只引用 Abstractions 与 `Seeing.Files`。`AddSeeingCore` 早于其他托管服务登记一行 `HookDirectoryService`。服务做目录快照并交给纯函数 `HookDirectoryCatalog` 差分；脚本经 `HookScriptBridge` 进单个 Node 工人，DLL 经影子副本进不回收的 `AssemblyLoadContext`。失败放行只包在脚本桥里。

**Tech Stack:** .NET 10、C# 13、系统 `PATH` 上的 `node`、`System.Text.Json`、xUnit v3、FluentAssertions、`Microsoft.Extensions.TimeProvider.Testing`（已在 CPM）

规格：`docs/superpowers/specs/2026-11-13-hook-directory-design.md`

## Global Constraints

- 目标框架 `net10.0`。新项目登记进 `Seeing.Agent.slnx`。不新增 NuGet 包，因此不改 `Directory.Packages.props`。
- `Seeing.Agent.Hooks.Directory` 只引用 `Seeing.Agent.Abstractions` 与 `Seeing.Files`。禁止引用 `Seeing.Agent.Core`。协议 JSON 不引用 `Seeing.Session`，不用 `SeeingJson`。
- `Seeing.Agent.Core` 仅增加项目引用，并在 `RegisterCoreServices` 开头加一行 `services.AddHostedService<HookDirectoryService>()`。两个 `AddSeeingCore` 重载都走这个方法，不要加第二处。
- 不改 `HookManager` 的触发策略、排序和「阻塞钩子抛异常即 `Continue: false`」。不改 `ToolManager`、`LlmInvoker`、`CommandService`。第 8 节那张调用方表是现状，不是本计划要修的行为。
- 不进 `BuiltInCapabilitySets`，不实现 `ISeeingModule`。
- 只扫 `ISeeingDirectories.UserSeeingDirectory` 下的 `hooks`。项目级目录不参与。
- 脚本失败放行：桥返回 `HookResult.Success`，不改 `mutable`，不向 `HookManager` 抛任何异常，包括 `OperationCanceledException`。DLL 异常继续冒泡。
- `HookScriptTimeout` 固定 `TimeSpan.FromSeconds(5)`，不是配置项。5 秒不含排队时间。超时只杀死当前进程；排队调用各自再启动。
- 去抖 400ms。测试禁止 `Task.Delay` / `Thread.Sleep` 用来等状态。超时时限用 `FakeTimeProvider.Advance`。
- 原子写只用 `AtomicFileWrite`。路径是否在根内只用 `PathBoundary.IsWithin`。
- 禁止对钩子点调用 `IHookManager.Clear`。
- 相对路径排序用 `StringComparer.Ordinal`。自动优先级从 `-1000` 起。
- 测试用 VSTest 直跑已构建程序集（本环境 MTP 发现不到测试）：

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~ClassName"
```

- 每个 Task 结束都 commit。信息用中文：`<type>(hooks): <摘要>`，空行后写为什么。
- 不新增 `docs/architecture` release note。

---

## 文件结构

### 新建

| 路径 | 职责 |
|---|---|
| `src/spine/Seeing.Agent.Hooks.Directory/Seeing.Agent.Hooks.Directory.csproj` | 程序集。嵌入 `hook-worker.js`。`InternalsVisibleTo` 测试项目 |
| `src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryOptions.cs` | `Enabled`，默认 `true` |
| `src/spine/Seeing.Agent.Hooks.Directory/HookPointNames.cs` | 文件名 → `HookRegistry` 常量；旧点名忽略大小写 |
| `src/spine/Seeing.Agent.Hooks.Directory/Projection/HookPayloadProjection.cs` | 投影与一次性写回 |
| `src/spine/Seeing.Agent.Hooks.Directory/Catalog/HookDirectoryCatalog.cs` | 分类、优先级、差分。不碰磁盘监听 |
| `src/spine/Seeing.Agent.Hooks.Directory/Scripts/INodeHookInvoker.cs` | 脚本调用口。桥和测试只依赖它 |
| `src/spine/Seeing.Agent.Hooks.Directory/Scripts/HookScriptBridge.cs` | 每个 `.js` 一个 `IHookHandler` |
| `src/spine/Seeing.Agent.Hooks.Directory/Scripts/NodeHookClient.cs` | 单工人、锁、`id`、超时、取消 |
| `src/spine/Seeing.Agent.Hooks.Directory/Scripts/INodeProcess.cs` | 可替换的 Node 进程 |
| `src/spine/Seeing.Agent.Hooks.Directory/Scripts/SystemNodeRuntime.cs` | `node -e` 探测与启动工人 |
| `src/spine/Seeing.Agent.Hooks.Directory/Scripts/hook-worker.js` | CommonJS 包装。协议独占 stdout |
| `src/spine/Seeing.Agent.Hooks.Directory/Dll/HookDllLoader.cs` | 影子副本、ALC、反射 |
| `src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryService.cs` | 快照、应用差分、停止 |
| `src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryWatcher.cs` | 去抖、单飞、祖先一层 |
| `tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj` | 测试项目 |
| `tests/spine/Seeing.Agent.Hooks.Directory.Fixtures/Seeing.Agent.Hooks.Directory.Fixtures.csproj` | 只引用 Abstractions 的处理器夹具。测试项目 `ReferenceOutputAssembly=false`，避免夹具提前进入默认加载上下文 |

### 修改

| 路径 | 改动 |
|---|---|
| `Seeing.Agent.slnx` | `/src/spine/` 加目录项目；`/tests/spine/` 加测试项目与夹具 |
| `src/spine/Seeing.Agent.Core/Seeing.Agent.Core.csproj` | `ProjectReference` 指向目录项目 |
| `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs` | `RegisterCoreServices` 第一行登记托管服务 |

---

### Task 1: 程序集与钩子点解析

**Files:**
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Seeing.Agent.Hooks.Directory.csproj`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryOptions.cs`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/HookPointNames.cs`
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj`
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookPointNamesTests.cs`
- Modify: `Seeing.Agent.slnx`
- Modify: `src/spine/Seeing.Agent.Core/Seeing.Agent.Core.csproj`

**Interfaces:**
- Produces: `HookDirectoryOptions.Enabled : bool`，默认 `true`
- Produces: `HookPointNames.IsLegacy(string point) : bool`。`OrdinalIgnoreCase`。五个名字：`chat.params`、`chat.headers`、`chat.message`、`chat.after_complete`、`chat.on_error`
- Produces: `HookPointNames.Resolve(string point) : HookSpec`。与 `HookRegistry` 里某个 `Point` 按 `Ordinal` 相等时返回那个常量；否则 `new HookSpec(HookPolicy.Blocking, point)`

- [ ] **Step 1: 写失败的测试**

`tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookPointNamesTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Hooks.Directory;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookPointNamesTests
{
    [Fact]
    public void Resolve_KnownPoint_UsesRegistrySpec()
    {
        var spec = HookPointNames.Resolve("tool.execute.before");
        spec.Should().Be(HookRegistry.ToolExecuteBefore);
        HookPointNames.Resolve("llm.message").Policy.Should().Be(HookPolicy.Parallel);
    }

    [Fact]
    public void Resolve_UnknownPoint_UsesBlockingPlaceholder()
    {
        var spec = HookPointNames.Resolve("no.such.point");
        spec.Point.Should().Be("no.such.point");
        spec.Policy.Should().Be(HookPolicy.Blocking);
    }

    [Theory]
    [InlineData("chat.params")]
    [InlineData("Chat.Params")]
    [InlineData("CHAT.ON_ERROR")]
    public void IsLegacy_IgnoresCase(string point)
    {
        HookPointNames.IsLegacy(point).Should().BeTrue();
    }

    [Fact]
    public void IsLegacy_NewName_IsFalse()
    {
        HookPointNames.IsLegacy("llm.params").Should().BeFalse();
    }
}
```

测试项目文件：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Hooks.Directory.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\spine\Seeing.Agent.Hooks.Directory\Seeing.Agent.Hooks.Directory.csproj" />
    <ProjectReference Include="..\..\..\src\spine\Seeing.Agent.Core\Seeing.Agent.Core.csproj" />
  </ItemGroup>
</Project>
```

产品项目先放一个能编译的空壳，否则测试项目无法引用。`HookPointNames` 先不要创建，让测试编译失败。空壳只需 csproj 和一个空的 `AssemblyMarker.cs`：

```csharp
namespace Seeing.Agent.Hooks.Directory;
public static class AssemblyMarker;
```

csproj：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Hooks.Directory</RootNamespace>
    <Description>用户级钩子目录：Node 脚本与 DLL 挂到 IHookManager</Description>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Seeing.Agent.Hooks.Directory.Tests" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <ProjectReference Include="..\..\abstractions\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj" />
    <ProjectReference Include="..\..\primitives\Seeing.Files\Seeing.Files.csproj" />
  </ItemGroup>
</Project>
```

`Seeing.Agent.slnx`：在 `/src/spine/` 的 Core 项目旁加目录项目；在 `/tests/spine/` 加测试项目。

`Seeing.Agent.Core.csproj` 的 `ProjectReference` 列表加：

```xml
<ProjectReference Include="..\Seeing.Agent.Hooks.Directory\Seeing.Agent.Hooks.Directory.csproj" />
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`HookPointNames` 不存在。

- [ ] **Step 3: 实现点名解析**

删除 `AssemblyMarker.cs`。

`HookDirectoryOptions.cs`：

```csharp
namespace Seeing.Agent.Hooks.Directory;

public sealed class HookDirectoryOptions
{
    public bool Enabled { get; set; } = true;
}
```

`HookPointNames.cs`：

```csharp
using System.Reflection;
using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Hooks.Directory;

public static class HookPointNames
{
    private static readonly string[] LegacyPoints =
    [
        "chat.params",
        "chat.headers",
        "chat.message",
        "chat.after_complete",
        "chat.on_error",
    ];

    private static readonly Dictionary<string, HookSpec> Known = Build();

    public static bool IsLegacy(string point) =>
        LegacyPoints.Contains(point, StringComparer.OrdinalIgnoreCase);

    public static HookSpec Resolve(string point)
    {
        if (Known.TryGetValue(point, out var spec))
            return spec;
        return new HookSpec(HookPolicy.Blocking, point);
    }

    private static Dictionary<string, HookSpec> Build()
    {
        var map = new Dictionary<string, HookSpec>(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        foreach (var field in typeof(HookRegistry).GetFields(flags))
        {
            if (field.GetValue(null) is HookSpec spec && !string.IsNullOrEmpty(spec.Point))
                map.TryAdd(spec.Point, spec);
        }
        return map;
    }
}
```

这五个旧名必须与 `HookManager` 的 `s_legacyLlmPoints` 一致。目录程序集不能引用 Core，所以在这里复制，并由上面的 Theory 锁住大小写。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookPointNamesTests"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Seeing.Agent.slnx src/spine/Seeing.Agent.Core/Seeing.Agent.Core.csproj src/spine/Seeing.Agent.Hooks.Directory tests/spine/Seeing.Agent.Hooks.Directory.Tests
git commit -m "feat(hooks): 建立钩子目录程序集与点名解析" -m "目录挂载要离开脊柱程序集，同时让文件名能对上 HookRegistry 里的原常量。"
```

---

### Task 2: JSON 投影与写回

**Files:**
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Projection/HookPayloadProjection.cs`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookPayloadProjectionTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `HookPayloadProjection.Project(IReadOnlyDictionary<string, object?>? bag, Action<string>? onDropped = null) : JsonObject`
  - `HookPayloadProjection.TryWrite(IDictionary<string, object?> mutable, JsonElement returned, out string? error) : bool`
  - 失败时 `mutable` 的键与值都与调用前同一引用内容。`JsonElement` 写回必须是 `Clone` 之后的值

- [ ] **Step 1: 写失败的测试**

```csharp
using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Hooks.Directory.Projection;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookPayloadProjectionTests
{
    [Fact]
    public void Project_UndefinedJsonElement_BecomesNull()
    {
        var bag = new Dictionary<string, object?> { ["args"] = default(JsonElement) };
        var json = HookPayloadProjection.Project(bag);
        json["args"]!.GetValueKind().Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Project_Exception_BecomesTypeAndMessage()
    {
        var json = HookPayloadProjection.Project(new Dictionary<string, object?>
        {
            ["error"] = new InvalidOperationException("boom")
        });
        json["error"]!["type"]!.GetValue<string>().Should().Be(typeof(InvalidOperationException).FullName);
        json["error"]!["message"]!.GetValue<string>().Should().Be("boom");
    }

    [Fact]
    public void Project_DropsLiveObjects()
    {
        var dropped = new List<string>();
        var json = HookPayloadProjection.Project(
            new Dictionary<string, object?> { ["token"] = new CancellationTokenSource() },
            dropped.Add);
        json.ContainsKey("token").Should().BeFalse();
        dropped.Should().Equal("token");
    }

    [Fact]
    public void TryWrite_JsonElement_Clones()
    {
        using var original = JsonDocument.Parse("{\"a\":1}");
        var bag = new Dictionary<string, object?> { ["args"] = original.RootElement };
        using var returned = JsonDocument.Parse("{\"args\":{\"b\":2}}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out var error).Should().BeTrue();
        error.Should().BeNull();
        var written = bag["args"].Should().BeOfType<JsonElement>().Subject;
        written.GetProperty("b").GetInt32().Should().Be(2);
        returned.Dispose();
        written.GetProperty("b").GetInt32().Should().Be(2);
    }

    [Fact]
    public void TryWrite_NullOntoUndefinedSlot_BecomesNullElement()
    {
        var bag = new Dictionary<string, object?> { ["args"] = default(JsonElement) };
        using var returned = JsonDocument.Parse("{\"args\":null}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        bag["args"].Should().BeOfType<JsonElement>().Which.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TryWrite_Headers_ReplacesWholeDictionary()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Keep"] = "1",
            ["Drop"] = "2"
        };
        var bag = new Dictionary<string, object?> { ["headers"] = headers };
        using var returned = JsonDocument.Parse("{\"headers\":{\"Keep\":\"9\"}}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        var written = bag["headers"].Should().BeAssignableTo<IDictionary<string, string>>().Subject;
        written.Should().NotContainKey("Drop");
        written["Keep"].Should().Be("9");
        written.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryWrite_MaxTokensFromNull_IsInt()
    {
        var bag = new Dictionary<string, object?> { ["maxTokens"] = null, ["temperature"] = null };
        using var returned = JsonDocument.Parse("{\"maxTokens\":128,\"temperature\":0.5}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        bag["maxTokens"].Should().Be(128);
        bag["temperature"].Should().Be(0.5d);
    }

    [Fact]
    public void TryWrite_InvalidValue_WritesNothing()
    {
        var bag = new Dictionary<string, object?> { ["proceed"] = true, ["arguments"] = "old" };
        using var returned = JsonDocument.Parse("{\"proceed\":\"no\",\"arguments\":\"new\"}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out var error).Should().BeFalse();
        error.Should().NotBeNull();
        bag["proceed"].Should().Be(true);
        bag["arguments"].Should().Be("old");
    }

    [Fact]
    public void TryWrite_AbsentKey_Stays()
    {
        var bag = new Dictionary<string, object?> { ["arguments"] = "old" };
        using var returned = JsonDocument.Parse("{}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        bag["arguments"].Should().Be("old");
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`HookPayloadProjection` 不存在。

- [ ] **Step 3: 实现投影**

`Projection/HookPayloadProjection.cs` 的规则：

1. `Project` 跳过 `null` 袋，返回空 `JsonObject`。
2. 值的顺序：`null`；`string`；`bool`；`JsonElement`（`Undefined` 写成 JSON `null`，其余 `Clone`）；`Exception` 写成 `{ "type", "message" }`；`byte`/`short`/`int`/`long` 写成整数，`float`/`double`/`decimal` 写成浮点；`IReadOnlyDictionary<string, object?>` 与 `IDictionary<string, string>` 递归成对象；`IEnumerable`（排除 `string`）递归成数组。其余调用 `onDropped` 并跳过该键。嵌套值不能投影时跳过嵌套键，不让整个袋失败。
3. `TryWrite` 只接受 JSON 对象。先把「袋里已经存在的键」全部转换成新值，任一失败立即返回 `false` 且不赋值。未知键忽略。
4. 转换表与规格第 6 节一致：
   - 当前值是 `JsonElement`（含 `Undefined`）：`Clone` 返回值。JSON `null` 得到 `ValueKind.Null`。
   - 当前值是 `IDictionary<string, string>`：返回值必须是对象且每个值都是字符串，否则失败。新建 `Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)`，内容等于返回对象。
   - 当前值是 `null`：字符串 / 布尔 / `null` 原样；`TryGetInt32` 成功则 `int`；其他数字是 `double`；对象或数组是 `Clone` 后的 `JsonElement`。
   - 当前值是字符串、布尔、整数、浮点：JSON 种类必须相同。数字之间用 `Convert.ToXxx(element.GetDouble())`，溢出则这个键失败。
5. `Clone` 的实现是 `JsonDocument.Parse(element.GetRawText()).RootElement.Clone()`，用完即释放 document。

`IDictionary<string, string>` 的判断要放在「普通对象字典」之前，避免被 `IEnumerable` 吃掉。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookPayloadProjectionTests"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/spine/Seeing.Agent.Hooks.Directory/Projection tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookPayloadProjectionTests.cs
git commit -m "feat(hooks): 投影钩子袋并一次性写回" -m "空的 JsonElement 和半截写回都会把工具调用收成钩子中断，写回必须先校验再落地。"
```

---

### Task 3: 目录差分

**Files:**
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Catalog/HookDirectoryCatalog.cs`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryCatalogTests.cs`

**Interfaces:**
- Consumes: `HookPointNames.Resolve`、`HookPointNames.IsLegacy`
- Produces:
  - `HookSnapshotFile`：`FullPath`、`RelativePath`（分隔符一律 `/`）、`Disposition`（`Script` / `EntryDll` / `Ignored`）、`Point`、`ExplicitPriority`、`ContentHash`
  - `ScriptBinding`：`FullPath`、`RelativePath`、`Point`、`Priority`、`Spec`
  - `DllBinding`：`FullPath`、`ContentHash`
  - `HookCatalogDiff`：`AddScripts`、`RemoveScriptPaths`、`AddDlls`、`RemoveDllPaths`
  - `HookDirectoryCatalog.Classify(string hooksRoot, string fullPath, bool withinBoundary, string? firstLine, string? contentHash) : HookSnapshotFile`
  - `HookDirectoryCatalog.Diff(IReadOnlyList<ScriptBinding> currentScripts, IReadOnlyList<DllBinding> currentDlls, IReadOnlyList<HookSnapshotFile> snapshot) : HookCatalogDiff`
  - `HookDirectoryCatalog.PathKey(string path) : string`。`Path.GetFullPath` 后去掉尾部分隔符。字典比较用 `StringComparer.FromComparison(PathBoundary.PathComparison)`

- [ ] **Step 1: 写失败的测试**

用临时目录的真实路径调用 `Classify`，不要启动监听。覆盖：

- `hooks/tool.execute.before.js` → `Script`，点名 `tool.execute.before`
- `hooks/tool.execute.before/audit.js` → 点名来自父目录
- `hooks/point/lib/a.js`、`hooks/node_modules/a.js`、`hooks/.cache/a.dll`、`hooks/a.cjs`、`hooks/a.mjs`、`hooks/sub/a.dll` → `Ignored`
- `withinBoundary: false` → `Ignored`
- 第一行 `// @priority 7` → `ExplicitPriority == 7`；第一行不是这个格式 → `null`
- `chat.params.js` 与 `Chat.Params.js` → `Ignored`
- `Diff`：已有 `c.js`，快照再加 `a.js`，两者优先级按 `Ordinal` 路径序且都小于 10。`A.js` 排在 `b.js` 前面
- 只改第一行优先级时，`RemoveScriptPaths` 与 `AddScripts` 都包含该路径，新 `Priority` 是新数字
- 正文变化（优先级不变、路径不变）时两个列表都不包含它
- DLL 哈希变化 → 先出现在 `RemoveDllPaths`，再出现在 `AddDlls`
- 哈希不变 → 两个 DLL 列表都没有它

`a.js` 与 `c.js` 的相对路径用同一钩子点目录，例如 `tool.execute.before/c.js` 与 `tool.execute.before/a.js`。

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`HookDirectoryCatalog` 不存在。

- [ ] **Step 3: 实现分类与差分**

`Classify`：

- `RelativePath = Path.GetRelativePath(hooksRoot, fullPath).Replace('\\', '/')`
- `withinBoundary == false` → `Ignored`
- 任一段是 `.cache`、`node_modules`，或以 `.` 开头 → `Ignored`
- 扩展名只认 `.js` 与 `.dll`（`OrdinalIgnoreCase`）
- `.js`：一段时点名是去掉扩展名的文件名；两段时点名是第一段；更深 → `Ignored`。`IsLegacy(point)` → `Ignored`。`ExplicitPriority` 用正则 `^// @priority (-?\d+)\s*$` 解析第一行去掉首尾空白后的整行，失败则 `null`
- `.dll`：只有一段才是 `EntryDll`，并带上调用方算好的 `ContentHash`
- `Spec` 不在 `Classify` 里算。`Diff` 给脚本调用 `HookPointNames.Resolve(point)`

`Diff` 的自动编号：

- 只取 `Disposition == Script` 且 `Point != null`
- 按 `Point` 用 `StringComparer.Ordinal` 分组
- 组内 `ExplicitPriority == null` 的按 `RelativePath`、`StringComparer.Ordinal` 排序，优先级 `-1000 + index`
- 显式优先级不参与编号
- 与 `currentScripts` 按 `PathKey` 比较。缺少的路径放进 `RemoveScriptPaths`。新路径放进 `AddScripts`。同一路径上 `Point` 或 `Priority` 变了：先放进移除，再放进新增
- DLL 按 `PathKey` 与 `ContentHash`（`Ordinal`）做同样的保持 / 替换 / 删除。`Ignored` 不产生绑定

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookDirectoryCatalogTests"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/spine/Seeing.Agent.Hooks.Directory/Catalog tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryCatalogTests.cs
git commit -m "feat(hooks): 按路径差分钩子目录" -m "后放入的脚本必须重算自动优先级，只改 @priority 也要先摘再挂。"
```

---

### Task 4: 脚本桥

**Files:**
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/INodeHookInvoker.cs`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/HookJson.cs`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/HookScriptBridge.cs`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookScriptBridgeTests.cs`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/RecordingHookManager.cs`

**Interfaces:**
- Consumes: `HookPayloadProjection.Project`、`TryWrite`、`HookPointNames.Resolve`
- Produces:
  - `enum ScriptCallStatus { Succeeded, Failed, Canceled }`
  - `record ScriptInvocationResult(ScriptCallStatus Status, bool Continue, JsonElement? Mutable, string? Error)`
  - `ScriptInvocationResult.Failure(string error)` → `Failed`，`Continue` 为 true
  - `ScriptInvocationResult.Cancel()` → `Canceled`
  - `interface INodeHookInvoker`：`bool EnsureAvailable()`；`Task<ScriptInvocationResult> InvokeAsync(string scriptPath, JsonElement eventPayload, CancellationToken cancellationToken)`；`Task StopAsync(CancellationToken cancellationToken)`
  - `HookScriptBridge(string scriptPath, HookSpec spec, int priority, INodeHookInvoker invoker, ILogger logger) : IHookHandler`
  - `RecordingHookManager`：记录 `Register` / `RegisterMulti` / `Remove`，`Clear` 递增计数。测试专用，放在测试项目

- [ ] **Step 1: 写失败的测试**

用真实 `HookManager`（`new HookManager(NullLogger<HookManager>.Instance)`）登记桥和一个 `Priority = 10` 的哨兵处理器。桥的优先级用 `-1000`。

- 调用器返回 `Failed`：哨兵的 `ExecuteAsync` 仍被调用；袋不变；`HookResult.Continue` 为 true
- 调用器返回 `Succeeded` 且 `Continue == false`，`mutable` 是合法 JSON：袋已写回，哨兵不被调用
- 调用器返回 `Succeeded`、`Continue == false`，同时 `mutable` 含非法值：返回 `Success`，袋不变，哨兵仍被调用
- `payload.Mutable["args"] = default(JsonElement)`，调用器返回 `Failed`：不抛，袋里仍是原来的 `Undefined` 元素，哨兵仍被调用
- 调用器返回 `Canceled`：返回 `Success`，不把取消记成失败（测试日志可用一个收集 Warning 的 `ILogger`，断言没有 Warning）
- `payload.CancellationToken` 已取消时，桥仍调用 `InvokeAsync` 并把该令牌传下去，由调用器决定返回 `Canceled`。桥自己不抛

哨兵：

```csharp
sealed class ProbeHandler : IHookHandler
{
    public HookSpec Spec { get; init; } = HookRegistry.ToolExecuteBefore;
    public int Priority => 10;
    public int Calls { get; private set; }
    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        Calls++;
        return Task.FromResult(HookResult.Success);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`HookScriptBridge` 不存在。

- [ ] **Step 3: 实现桥**

`InvokeAsync` 之前用 `Project` 做 `input` / `mutable` / `result`。投影抛错时记 Warning 并返回 `HookResult.Success`，不调用 Node。

事件对象的键固定为 `point`、`sessionId`、`messageId`、`step`、`input`、`mutable`、`result`。`sessionId` 用 `payload.SessionId ?? ""`。序列化成 `JsonElement` 时使用下面的选项，不设 `PropertyNamingPolicy`：

```csharp
internal static class HookJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
```

`InvokeAsync` 的结果：

- `Canceled` → `HookResult.Success`，不记 Warning，不写回
- `Failed` → Warning（带脚本路径和 `payload.Spec.Point`），`HookResult.Success`
- `Succeeded` 且 `Mutable` 缺席或 `ValueKind` 为 `Undefined` / `Null` → 不写回。`Continue == false` 时返回 `HookResult.Stop`，否则 `Success`
- `Succeeded` 且 `Mutable` 是对象 → `TryWrite`。失败则 Warning 并返回 `Success`，即使 `Continue == false`。成功后再按 `Continue` 返回 `Stop` 或 `Success`
- 任何其他异常 → Warning 并返回 `Success`

`EnsureAvailable` 与 `StopAsync` 在本任务的假实现里就位，真 Node 留到 Task 5。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookScriptBridgeTests"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/spine/Seeing.Agent.Hooks.Directory/Scripts/INodeHookInvoker.cs src/spine/Seeing.Agent.Hooks.Directory/Scripts/HookScriptBridge.cs src/spine/Seeing.Agent.Hooks.Directory/Scripts/HookJson.cs tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookScriptBridgeTests.cs tests/spine/Seeing.Agent.Hooks.Directory.Tests/RecordingHookManager.cs
git commit -m "feat(hooks): 脚本桥失败时放行" -m "投影异常和非法写回不能冒泡，否则 HookManager 会把这次工具调用收成中断。"
```

---

### Task 5: Node 工人

**Files:**
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/hook-worker.js`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/HookWorkerFiles.cs`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/INodeProcess.cs`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/SystemNodeRuntime.cs`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Scripts/NodeHookClient.cs`
- Modify: `src/spine/Seeing.Agent.Hooks.Directory/Seeing.Agent.Hooks.Directory.csproj`（嵌入工人脚本）
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/NodeHookClientTests.cs`

**Interfaces:**
- Consumes: `INodeHookInvoker`、`ScriptInvocationResult`、`HookJson`
- Produces:
  - `NodeHookClient : INodeHookInvoker`
  - `public const TimeSpan HookScriptTimeout = TimeSpan.FromSeconds(5)`
  - 构造：`NodeHookClient(INodeRuntime runtime, Func<string> workerPath, TimeProvider time, ILogger logger)`。`workerPath` 在每次真正 `StartWorker` 之前调用，返回工人脚本的绝对路径
  - `HookWorkerFiles.Ensure(string cacheDirectory) : string`。从嵌入资源读出 `hook-worker.js`，SHA256 小写十六进制，写到 `cacheDirectory/hook-worker-<hash>.js`。已存在且字节一致则直接返回该路径。写入用 `AtomicFileWrite.WriteAllTextAtomic`
  - `interface INodeRuntime`：`bool Probe()`；`INodeProcess StartWorker(string workerPath)`
  - `interface INodeProcess`：`Task WriteLineAsync(string line, CancellationToken cancellationToken)`；`Task<string?> ReadLineAsync(CancellationToken cancellationToken)`；`void Kill()`；`ValueTask DisposeAsync()`
  - `SystemNodeRuntime`：`Probe` 执行 `node`，参数列表是 `-e` 与 `process.exit(0)`，不经 shell。`StartWorker` 同样不经 shell，重定向 stdin/stdout/stderr

- [ ] **Step 1: 写失败的测试**

假 `INodeProcess` 用 `Channel<string>` 做 stdout。测试可以 `Enqueue(line)`，`ReadLineAsync` 从通道读。`Kill` 完成通道并递增计数。假 `INodeRuntime.StartWorker` 每次返回新进程并记下次数。

用 `FakeTimeProvider`：

- stdout 先来一行 `not json`，再来 `{"id":"其他","ok":true}`，再来匹配当前请求的 `{"id":"<本次>","ok":true,"continue":true}`。结果是 `Succeeded`。断言写出的请求含小写键 `id`、`script`、`event`
- 两个 `InvokeAsync` 并发。第一个进程不回复。`Advance(5s)` 后第一个结果是 `Failed`，`Kill` 被调用。第二个在新进程上得到匹配响应，结果是 `Succeeded`，不是 `Failed`
- 调用方令牌取消：结果是 `Canceled`，`Kill` 仍是 0。随后往旧进程塞旧 `id` 的成功响应，再发起下一次调用，下一次要配上自己的新 `id`
- `Probe` 返回 false 之后，`EnsureAvailable` 一直为 false，`StartWorker` 次数为 0

推进虚拟时间后 `await Task.Yield()`，与 `RenderCoalescerTests.AdvanceAsync` 相同。不要 `Task.Delay`。

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`NodeHookClient` 不存在。

- [ ] **Step 3: 实现客户端与工人**

csproj 增加：

```xml
<ItemGroup>
  <EmbeddedResource Include="Scripts\hook-worker.js" />
</ItemGroup>
```

`hook-worker.js` 全文使用下面的契约：

- 开头保存 `process.stdout.write.bind(process.stdout)` 为 `realStdoutWrite`
- 把 `console.log` / `info` / `debug` / `warn` 和 `process.stdout.write` 转到 stderr
- 协议行只用 `realStdoutWrite(JSON.stringify(obj) + "\n")`
- 用 `readline` 读 stdin。解析失败的行直接忽略
- `const { createRequire } = require("module")`。对请求里的绝对路径 `createRequire(script)`，先 `delete requireCache[resolve(script)]`，再加载
- `module.exports` 不是函数，或返回值不是对象也不是 `null`/`undefined`：`{ id, ok:false, error }`
- `null`/`undefined`：`{ id, ok:true, continue:true }`
- 对象：`continue` 缺省为 true；带上 `mutable`
- 用户函数抛错：`{ id, ok:false, error: message }`

`NodeHookClient`：

- `SemaphoreSlim(1, 1)` 包住「写请求 + 等这条响应」。`WaitAsync(CancellationToken.None)`，排队时间不占 5 秒。`finally` 释放
- 进入临界区后若调用方令牌已取消，返回 `Canceled`，不启动进程
- 先把 `TaskCompletionSource` 放进按 `id` 索引的字典，再写请求。`id` 用递增整数的十进制字符串
- 等结果用 `timeProvider.CreateCancellationTokenSource(HookScriptTimeout)`，不要把调用方令牌链进这个超时源
- `await tcs.Task.WaitAsync(timeoutToken)`。超时：`Kill()`，把进程引用置空，返回 `Failure("timeout")`
- 调用方取消：从字典移除 `id`，返回 `Canceled`，不 `Kill`
- 单独的读循环读 stdout。非 JSON 记 Debug。`id` 不在字典里记 Debug 并丢掉。匹配则 `TrySetResult`
- 读到 `null` 或进程已杀死：把剩余等待全部 `TrySetResult(Failure("Node 进程已退出"))`
- 下一次 `InvokeAsync` 发现进程引用为空时 `StartWorker`
- `EnsureAvailable`：只探测一次。失败记一条 Warning，之后一直返回 false，且不启动工人
- `StopAsync`：关闭写入并 `Kill`。不等待测试里不存在的真实进程超时；生产 `SystemNodeRuntime` 在 `DisposeAsync` 里等最多 2 秒（`TimeProvider`），到点 `Kill`

`SystemNodeRuntime.Probe` 捕获 `Win32Exception` 与非零退出码，返回 false。不要把「找不到 node」抛给调用方。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~NodeHookClientTests"
```

Expected: PASS。机器没有安装 Node 也必须通过，因为测试不调用 `SystemNodeRuntime`。

- [ ] **Step 5: Commit**

```bash
git add src/spine/Seeing.Agent.Hooks.Directory/Scripts src/spine/Seeing.Agent.Hooks.Directory/Seeing.Agent.Hooks.Directory.csproj tests/spine/Seeing.Agent.Hooks.Directory.Tests/NodeHookClientTests.cs
git commit -m "feat(hooks): 用单 Node 工人执行钩子脚本" -m "脚本日志不能占用协议 stdout，超时也不能把正在排队的下一次调用一起跳过。"
```

---

### Task 6: DLL 影子加载

**Files:**
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.Fixtures/Seeing.Agent.Hooks.Directory.Fixtures.csproj`
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.EmptyFixture/Seeing.Agent.Hooks.Directory.EmptyFixture.csproj`
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.EmptyFixture/EmptyMarker.cs`
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.Fixtures/SampleHook.cs`
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.Fixtures/ThrowingHook.cs`
- Create: `tests/spine/Seeing.Agent.Hooks.Directory.Fixtures/MultiAndSingleHook.cs`
- Create: `src/spine/Seeing.Agent.Hooks.Directory/Dll/HookDllLoader.cs`
- Modify: `Seeing.Agent.slnx`
- Modify: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDllLoaderTests.cs`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/FixturePaths.cs`

**Interfaces:**
- Consumes: `AtomicFileWrite.WriteAllBytesAtomic`、`IHookHandler`、`IMultiHookHandler`
- Produces: `HookDllLoader.Load(string sourceDllPath, string cacheDirectory, ILogger logger) : LoadedHookAssembly`
- `LoadedHookAssembly`：`SourcePath`、`CachePath`、`ContentHash`、`Handlers`、`Multis`、`Skip`（`None` / `Companion` / `Failed`）、`Message`、`LoadContext`（服务持有它，避免上下文被回收；上下文本身不卸载）

- [ ] **Step 1: 写夹具和失败的测试**

夹具只引用 Abstractions。三个公开类型都有无参构造：

- `SampleHook : IHookHandler`，`Spec` 为 `HookRegistry.ToolExecuteBefore`，`Priority` 为 40，`ExecuteAsync` 返回 `Success`
- `ThrowingHook : IHookHandler`，同一点，`ExecuteAsync` 抛 `InvalidOperationException`
- `MultiAndSingleHook : IHookHandler, IMultiHookHandler`。`Specs` 只有 `HookRegistry.LlmParams`。加载结果里它只出现在 `Multis`

测试项目引用夹具时：

```xml
<ProjectReference Include="..\Seeing.Agent.Hooks.Directory.Fixtures\Seeing.Agent.Hooks.Directory.Fixtures.csproj">
  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
</ProjectReference>
```

`FixturePaths.HandlerDll()` 从 `AppContext.BaseDirectory`（`.../bin/<Configuration>/net10.0/`）往上四级，进入 `Seeing.Agent.Hooks.Directory.Fixtures/bin/<Configuration>/net10.0/`。`<Configuration>` 是 `net10.0` 这一级的父目录名。文件不存在就抛 `FileNotFoundException`，不要跳过测试。

测试：

- 把夹具 DLL 复制到临时 `hooks/sample.dll`。`Load` 之后 `File.Delete` 原文件成功，`Handlers` 里能执行 `SampleHook`
- `CachePath` 在 `hooks/.cache/<sha256>.dll`。预先用 `AtomicFileWrite` 把该路径写成字节 `1,2,3`。`Load` 之后缓存字节与源 DLL 一致
- 另建 `tests/spine/Seeing.Agent.Hooks.Directory.EmptyFixture`，里面只有一个公开空类，不引用钩子接口。同样 `ReferenceOutputAssembly=false`。把它复制到 `hooks/extra.dll` 再 `Load`。`Skip` 为 `Companion`，`Handlers` 与 `Multis` 都空。不要拿 `Seeing.Agent.Abstractions.dll` 当伴随 DLL，那会在加载上下文里再装一份契约程序集
- 复制夹具后把 `ThrowingHook` 登记进真实 `HookManager`，`TriggerBlockingAsync(HookRegistry.ToolExecuteBefore, ...)` 的 `Continue` 为 false
- `MultiAndSingleHook` 只出现一次，且在 `Multis`

夹具程序集里同时有三个类型，一次 `Load` 会把它们都实例化。断言用类型名，不引用夹具命名空间里的类型（测试项目没有编译期引用）。

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`HookDllLoader` 不存在。夹具项目本身能单独 `dotnet build`。

- [ ] **Step 3: 实现加载器**

1. 读取源字节，SHA256 小写十六进制作为 `ContentHash`。
2. 目标 `cacheDirectory/<hash>.dll`。文件已存在则读出再哈希；不一致就用 `AtomicFileWrite.WriteAllBytesAtomic` 重写。一致则直接用。
3. `new AssemblyLoadContext(hash, isCollectible: false)`。`Resolving`：默认上下文已有同名程序集则返回那一份；否则 `AssemblyDependencyResolver(sourceDllPath)` 的路径，`LoadFromAssemblyPath`。
4. `LoadFromAssemblyPath(cachePath)`。`GetTypes` 抛 `ReflectionTypeLoadException` 时 `Skip = Failed`，`Message` 为异常信息，记 Warning。
5. 公开、非抽象、有公开无参构造。同时实现两个接口时只 `CreateInstance` 一次并放入 `Multis`。只有 `IHookHandler` 时放入 `Handlers`。
6. 一个合格类型都没有：`Skip = Companion`，记一次 Debug，不记 Warning。
7. 复制或反射前的 IO 异常：`Skip = Failed`，记 Warning。
8. 返回对象持有 `LoadContext` 字段，调用方必须保住引用。不要调用 `Unload`。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookDllLoaderTests"
```

Expected: PASS。删除原入口 DLL 的断言在 Windows 上必须成功。依赖仍从源路径解析，本任务不断言能删掉依赖。

- [ ] **Step 5: Commit**

```bash
git add Seeing.Agent.slnx src/spine/Seeing.Agent.Hooks.Directory/Dll tests/spine/Seeing.Agent.Hooks.Directory.Fixtures tests/spine/Seeing.Agent.Hooks.Directory.Tests
git commit -m "feat(hooks): 影子加载钩子 DLL" -m "Windows 会锁住直接 LoadFrom 的文件，入口程序集必须先原子写到缓存再加载。"
```

---

### Task 7: 目录服务

**Files:**
- Create: `src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryService.cs`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryServiceTests.cs`

**Interfaces:**
- Consumes: `HookDirectoryCatalog`、`HookScriptBridge`、`HookDllLoader`、`INodeHookInvoker`、`ISeeingDirectories`、`HookDirectoryOptions`、`RecordingHookManager`
- Produces:
  - `HookDirectoryService : IHostedService`
  - 生产构造：`(IHookManager hooks, ISeeingDirectories directories, IOptions<HookDirectoryOptions> options, ILoggerFactory loggerFactory)`。它调用测试构造，并传入：

```csharp
new NodeHookClient(
    new SystemNodeRuntime(),
    () => HookWorkerFiles.Ensure(Path.Combine(directories.UserSeeingDirectory, "hooks", ".cache")),
    TimeProvider.System,
    loggerFactory.CreateLogger<NodeHookClient>())
```

  - 测试构造：`(IHookManager, ISeeingDirectories, IOptions<HookDirectoryOptions>, INodeHookInvoker, ILogger<HookDirectoryService>)`，`internal`
  - `internal Task SynchronizeAsync(CancellationToken cancellationToken)`
  - 生产构造在 `StartAsync` 里调用 `SynchronizeAsync`。`Enabled == false` 时 `StartAsync` 直接返回

- [ ] **Step 1: 写失败的测试**

`ISeeingDirectories` 的假实现返回两个临时目录。用户目录里放 `tool.execute.before/audit.js`，项目目录里放同名文件。假调用器 `EnsureAvailable` 为 true。

- `SynchronizeAsync` 后 `RecordingHookManager` 只登记了用户目录那一个桥，`Clear` 次数为 0
- 删掉该 js 再同步，`Remove` 收到同一个桥实例
- `Enabled = false` 时 `StartAsync` 不登记，假调用器的 `EnsureAvailable` 不被调用
- 用户目录里同时有一个夹具 DLL 副本与一个 js。假 `EnsureAvailable` 返回 false。结果只登记 DLL 处理器，不登记桥
- 服务只摘自己登记过的实例。预先用 `RecordingHookManager` 无法区分「别人的处理器」时，改为真实 `HookManager`：先 `Register` 一个不由服务创建的哨兵，`StopAsync` 之后哨兵仍在，`Count` 仍为 1，目录桥已不在
- 符号链接：在临时目录外写 `outside.js`，在 `hooks` 里 `File.CreateSymbolicLink` 指向它。同步后不登记。创建链接抛异常则让测试失败，不要 `Skip`

js 文件内容可以是空文件。服务不执行它，只按扩展名登记桥。

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`HookDirectoryService` 不存在。

- [ ] **Step 3: 实现服务**

根路径：`Path.Combine(directories.UserSeeingDirectory, "hooks")`。目录不存在则快照为空，不创建它。

枚举：手写栈，只进入根的直接子目录。跳过名为 `.cache`、`node_modules` 或以 `.` 开头的目录。每个文件先 `PathBoundary.IsWithin(fullPath, root)`，再读 `.js` 的第一行，`.dll` 计算 SHA256。然后 `Classify`。

应用差分的顺序：

1. 脚本快照非空时才 `EnsureAvailable`。返回 false 则这次参与差分的脚本列表为空。DLL 列表不动
2. `RemoveScriptPaths`：`Remove` 对应桥
3. `AddScripts`：`new HookScriptBridge` 后 `Register`
4. DLL 同样先摘后挂。摘的时候只 `Remove` 上次 `Load` 返回的那些实例。新的 `LoadedHookAssembly` 存在服务的列表里，保住 `LoadContext`
5. 全程不调用 `Clear`

`StopAsync`：按登记记录反向 `Remove`，再 `await invoker.StopAsync`。在飞调用由桥的失败放行收场，服务不再补一次触发。

`StartAsync` 重复调用直接返回。用一个 `int _started` 的 `Interlocked.Exchange`。

本任务不创建文件监听。`StartAsync` 只做一次同步。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookDirectoryServiceTests"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryService.cs tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryServiceTests.cs
git commit -m "feat(hooks): 把用户目录差分登记进 HookManager" -m "项目目录里的同名脚本不能挂上，关掉开关时也不能去探测本机的 node。"
```

---

### Task 8: 监听与宿主登记

**Files:**
- Create: `src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryWatcher.cs`
- Modify: `src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryService.cs`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`（`RegisterCoreServices` 方法体的第一行）
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryWatcherTests.cs`
- Test: `tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryRegistrationTests.cs`

**Interfaces:**
- Consumes: `HookDirectoryService.SynchronizeAsync`
- Produces: `HookDirectoryWatcher(string hooksRoot, Func<CancellationToken, Task> onRescan, TimeProvider time, ILogger logger) : IDisposable`
- `internal void ArmForTests()`：跳过去抖，直接进入单飞入口。生产代码的文件事件只调用去抖，不调用这个方法

- [ ] **Step 1: 写失败的测试**

监听测试不创建 `FileSystemWatcher`，只调用 `ArmForTests`：

- `onRescan` 第一次停在 `TaskCompletionSource` 上。再 `ArmForTests` 一次。完成第一次后，`onRescan` 的调用次数变为 2，然后停住。没有第三次
- `Dispose` 之后 `ArmForTests` 不再调用 `onRescan`

登记测试：

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddSeeingCore(new ConfigSectionRegistry());
services.Should().Contain(d =>
    d.ServiceType == typeof(IHostedService)
    && d.ImplementationType == typeof(HookDirectoryService));
```

不要 `Host.Start`。不要解析 `HookDirectoryService`，否则会去碰开发者的真实用户目录。

`AddHostedService<AgentManager>(工厂)` 的描述符没有 `ImplementationType`。不要靠它找下标。断言集合里第一个 `IHostedService` 的 `ImplementationType` 就是 `HookDirectoryService`。`AddSeeingCore` 在 `RegisterCoreServices` 之前不登记托管服务，所以第一项就是目录服务，`StartAsync` 排在后面的 `AgentManager` 之前。

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
```

Expected: 编译失败，`HookDirectoryWatcher` 不存在；登记断言失败。

- [ ] **Step 3: 实现监听并登记服务**

`HookDirectoryWatcher`：

- 根已存在：`FileSystemWatcher` 监听该目录，`IncludeSubdirectories = true`，`NotifyFilter` 含文件名、目录名、最后写入、大小
- 根不存在：`NearestExistingDirectory` 只监听那一层，`IncludeSubdirectories = false`。事件的路径必须等于「祖先 + 通向 hooks 的下一段」，否则忽略。回调开始前若根已经出现，改挂到根本身
- `InternalBufferSize = 32 * 1024`。`Error` 事件与普通变更一样只是再发一次「请重扫」
- 去抖：`TimeProvider.CreateTimer`，间隔 400ms，不重复。新事件重置计时
- 单飞：`_running` 为 1 时把 `_again` 置 1 并返回。回调在 `do/while` 里看到 `_again` 就再跑一轮。`Dispose` 取消令牌并不再进入回调
- 回调异常只记 Warning，不抛出

`HookDirectoryService.StartAsync` 在首次同步成功后创建 watcher，回调就是 `SynchronizeAsync`。`Enabled == false` 不创建 watcher。`StopAsync` 先 `Dispose` watcher，再摘处理器、停 Node。

`RegisterCoreServices` 的第一行，早于现有任何一个 `AddHostedService`：

```csharp
services.AddHostedService<Seeing.Agent.Hooks.Directory.HookDirectoryService>();
```

不要把目录服务放进 `InitializeSeeingAsync`。`Activate` 期间的 `tool.before_register` 与 `mcp.before_connect` 看不见目录钩子，这是规格里的已知边界，不要为了赶上它们去改启动顺序以外的代码。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Hooks.Directory.Tests/Seeing.Agent.Hooks.Directory.Tests.csproj
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll --TestCaseFilter:"FullyQualifiedName~HookDirectory"
```

Expected: `HookDirectoryWatcherTests`、`HookDirectoryServiceTests`、`HookDirectoryRegistrationTests` 全部 PASS。

再跑本测试项目全量：

```bash
dotnet vstest tests/spine/Seeing.Agent.Hooks.Directory.Tests/bin/Debug/net10.0/Seeing.Agent.Hooks.Directory.Tests.dll
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryWatcher.cs src/spine/Seeing.Agent.Hooks.Directory/HookDirectoryService.cs src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryWatcherTests.cs tests/spine/Seeing.Agent.Hooks.Directory.Tests/HookDirectoryRegistrationTests.cs
git commit -m "feat(hooks): 监听用户钩子目录并随核心启动" -m "宿主只多一行托管服务，启动时的模块 Activate 仍然先于目录挂载。"
```

---

## 规格覆盖

| 规格 | 任务 |
|---|---|
| 只扫用户目录，项目级不挂 | Task 7 |
| `StartAsync` 返回后才可见；不改 Activate | Task 8 的登记位置与已知边界 |
| `.js` / 顶层 `.dll`，忽略 `.cjs` `.mjs` `node_modules` `.cache` 两层嵌套 | Task 3 |
| `PathBoundary`，符号链接越界跳过 | Task 3 的标志位，Task 7 的真实链接 |
| 旧点名忽略大小写 | Task 1、Task 3 |
| `createRequire`，stdout 只跑协议 | Task 5 工人脚本 |
| 5 秒不含排队，排队调用再启动 | Task 5 |
| 取消作废 `id`，不杀进程，桥不抛 | Task 4、Task 5 |
| `Undefined` 的 `args` 不打断链 | Task 2、Task 4 |
| 字典整袋替换，失败不写任何键 | Task 2 |
| 自动优先级重算，`@priority` 变更先摘再挂 | Task 3 |
| 影子副本、残文件重写、删原入口 DLL、伴随 DLL 记 Debug | Task 6 |
| DLL 抛异常则 `Continue: false` | Task 6 |
| 失败放行时下一个处理器仍跑；非法写回加 `continue: false` 也不打断 | Task 4 |
| 不调用 `Clear`，不摘别人的处理器 | Task 7 |
| `Enabled == false` 不扫描不探测 | Task 7 |
| 监听单飞且进行中的信号会再跑一轮 | Task 8 |
| 一行 `AddHostedService`，排在其他托管服务之前 | Task 8 |
