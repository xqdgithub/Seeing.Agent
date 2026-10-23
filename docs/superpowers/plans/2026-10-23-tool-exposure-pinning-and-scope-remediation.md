# 工具钉选与执行级可见性收口 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让分层暴露默认钉住常用工具，并完成架构评审提出的可见性收口（单一快照、声明式加载、契约补齐、Scheduler 提升、文案一致）。

**Architecture:** 钉选保持"代码默认集 + 配置显式覆盖 + Agent 覆盖"三层，不引入新存储与 UI。执行期继续用 `IExecutionToolScope` 承载可调用性，把"加载"从能力包直接写状态改为 `ToolResult.LoadedToolIds` 声明、执行器统一应用；`tool_search` 语料改取作用域里的冻结 deferred 集；会话暴露状态的提升/提交统一经 `ISessionToolExposureWriter`。

**Tech Stack:** .NET 10 / C#、xUnit + FluentAssertions + Moq、Blazor（本次不改 UI）。

**Spec:** `docs/superpowers/specs/2026-10-23-tool-exposure-pinning-and-scope-remediation-design.md`

## Global Constraints

- 目标框架 `.NET 10.0`；不新增第三方依赖。
- Core 瘦脊柱（零工具）；能力包只依赖 Abstractions + 原语；Abstractions **零实现**。
- schema 唯一计算点仍是 `SessionToolSchemaResolver`（Hosting）；executor 不得自算 schema。
- 会话暴露状态写入只允许出现在 `ISessionToolExposureWriter` 实现、原语层与 `SessionToolDiscoveryStore`。
- 代码注释与用户可见文案一律中文。
- 测试运行方式（MTP 无法发现测试）：`dotnet build <测试项目>` 后 `dotnet vstest <bin/Debug/net10.0/xxx.dll> --TestCaseFilter:"FullyQualifiedName~<类名>"`。
- 术语统一：**本次执行内可调用** / **下一次执行起进入 `tools[]`**（禁止再写"下一轮生效"）。
- 默认钉选集固定为 16 项，顺序即内联顺序：`read, write, edit, glob, grep, bash, delete, open_file, add_workspace_path, question, skill, task, todowrite, webfetch, websearch, codesearch`（`memory_*` 不默认钉选）。

## Review Focus

1. `config.PinnedTools` 非空时必须**整体替换**默认集，而不是追加。
2. `Agent.PinnedTools = []` 时必须**完全不钉**，不能回落到默认集。
3. 工具返回 `LoadedToolIds` 但未声明 `exposure.load` 能力键时，必须**被忽略**（防止第三方工具解锁）。
4. 无执行作用域（`ToolContext.ToolScope == null`）时，`tool_search` 文案必须降级为"下一次执行起"，不得承诺本次可用。
5. Scheduler 会话不存在/加载失败时，`PromotePendingAsync` 必须返回 `false` 且**不抛异常**，任务继续。

---

### Task 1: 默认钉选集（含 C1 回归防护）

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureConfigResolver.cs`
- Modify: `src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs`（仅 XML 注释）
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureConfigResolverTests.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/ToolExposureDefaultsTests.cs`（重写）

**Interfaces:**
- Produces: `public static readonly IReadOnlyList<string> ToolExposureConfigResolver.DefaultPinnedTools`（16 项，顺序见 Global Constraints）。
- Produces: `EffectiveExposureConfig.PinnedTools` 语义 = 最终钉选集（Agent 覆盖 → 配置非空 → 默认集）。

- [ ] **Step 1: 写失败测试（Hosting）**

在 `ToolExposureConfigResolverTests` 增加：

```csharp
[Fact]
public void Resolve_NoOverrides_ShouldUseDefaultPinnedTools()
{
    var resolver = CreateResolver(new SeeingAgentOptions());
    var result = resolver.Resolve(null, null, null);
    result.PinnedTools.Should().Equal(ToolExposureConfigResolver.DefaultPinnedTools);
}

[Fact]
public void Resolve_NonEmptyConfigPinned_ShouldReplaceDefaults()
{
    var options = new SeeingAgentOptions();
    options.Tools.Exposure.PinnedTools = ["custom_a", "custom_b"];
    var result = CreateResolver(options).Resolve(null, null, null);
    result.PinnedTools.Should().Equal("custom_a", "custom_b");
}

[Fact]
public void Resolve_AgentPinnedEmpty_ShouldPinNothing()
{
    var options = new SeeingAgentOptions();
    options.Tools.Exposure.PinnedTools = ["custom_a"];
    var agent = new AgentDefinition { Name = "a", PinnedTools = [] };
    var result = CreateResolver(options).Resolve(null, agent, null);
    result.PinnedTools.Should().BeEmpty();
}
```

（`CreateResolver` 复用现有测试里的 `IOptionsMonitor` mock 写法；若不存在则新增一个私有 helper。）

- [ ] **Step 2: 运行并确认失败**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests`；`dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolExposureConfigResolverTests"`
Expected: 编译失败（`DefaultPinnedTools` 不存在）。

- [ ] **Step 3: 实现默认集与替换语义**

`ToolExposureConfigResolver` 增加常量与解析：

```csharp
public static readonly IReadOnlyList<string> DefaultPinnedTools = [ /* 16 项，顺序同 Global Constraints */ ];
```

`Resolve` 中把现有 `agent?.PinnedTools ?? exposure.PinnedTools ?? []` 改为：

```csharp
IReadOnlyList<string> pinned = agent?.PinnedTools
    ?? (exposure.PinnedTools is { Count: > 0 } ? exposure.PinnedTools : DefaultPinnedTools);
```

`SeeingAgentOptions.ToolExposureOptions.PinnedTools` 保持 `= []`，XML 注释改为：
"非空时整体替换默认钉选集；空数组表示未设置（使用代码默认集）。"

- [ ] **Step 4: 重写 spine 的默认值测试（C1 回归）**

`tests/spine/Seeing.Agent.Tests/Configuration/ToolExposureDefaultsTests.cs` 改为：

```csharp
[Fact]
public void OptionsPinnedTools_Default_ShouldBeEmpty_ToAvoidMergeOverride()
{
    new SeeingAgentOptions().Tools.Exposure.PinnedTools.Should().BeEmpty();
}

[Fact]
public void Merge_UserPinnedWithDefaultProject_ShouldKeepUserValue()
{
    var user = new SeeingAgentOptions();
    user.Tools.Exposure.PinnedTools = ["custom"];
    var merged = MergeDeep.Merge(user, new SeeingAgentOptions());
    merged.Tools.Exposure.PinnedTools.Should().Equal("custom");
}
```

（`MergeDeep` 在 `Seeing.Agent.Core.Configuration`；测试项目已有 InternalsVisibleTo。）

- [ ] **Step 5: 运行两个测试项目**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests && dotnet vstest .../Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolExposureConfigResolverTests"`
Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest .../Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolExposureDefaultsTests"`
Expected: 全部 PASS。

- [ ] **Step 6: Commit**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureConfigResolver.cs \
        src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs \
        tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureConfigResolverTests.cs \
        tests/spine/Seeing.Agent.Tests/Configuration/ToolExposureDefaultsTests.cs
git commit -m "feat(tool-exposure): 默认钉选常用工具，默认集移出配置初始化器"
```

---

### Task 2: 声明式加载（ToolResult.LoadedToolIds + exposure.load）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolResult.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolCapabilityKeys.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/ToolSearchToolTests.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Core/AgentExecutorToolVisibilityTests.cs`

**Interfaces:**
- Consumes: `IExecutionToolScope.TryLoad(IEnumerable<string>) → int`、`IExecutionToolScope.IsCallable(string) → bool`。
- Produces: `ToolResult.LoadedToolIds : IReadOnlyList<string>?`
- Produces: `ToolCapabilityKeys.ExposureLoad = "exposure.load"`
- Produces: `AgentExecutor.ApplyDeclaredToolLoads(string toolName, ToolResult result, IExecutionToolScope toolScope) → void`（private）

- [ ] **Step 1: 写失败测试（ToolSearchTool）**

在 `ToolSearchToolTests` 中：作用域存在时，`detail=full` 必须设置 `LoadedToolIds` 且不再直接写作用域（`FakeScope.LoadedIds` 保持空）。同时把既有 `FullDetail_ShouldStateSameRequestCallability` 里对 `_scope.LoadedIds` 的断言删除（该行为将由执行器承担）。

```csharp
[Fact]
public async Task DetailFull_ShouldDeclareLoadedToolIds_NotWriteScopeDirectly()
{
    var tool = CreateTool(Entry("file_read"));
    var result = await tool.ExecuteAsync(Args(new { query = "file_read", detail = "full" }), _context);
    result.LoadedToolIds.Should().Equal("file_read");
    _scope.LoadedIds.Should().BeEmpty();
}

[Fact]
public async Task DetailFull_WhenDiscoveryStoreFails_ShouldNotDeclareLoaded()
{
    _store.Setup(s => s.Add(It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(false);
    var tool = CreateTool(Entry("file_read"));
    var result = await tool.ExecuteAsync(Args(new { query = "file_read", detail = "full" }), _context);
    result.LoadedToolIds.Should().BeNull();
    result.Output.Should().Contain("登记失败");
}
```

- [ ] **Step 2: 运行并确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests`
Expected: 编译失败（`LoadedToolIds` 不存在）。

- [ ] **Step 3: 加契约与能力键**

`ToolResult` 增加：

```csharp
/// <summary>工具声明"本次执行内已加载"的工具 id（仅声明 exposure.load 能力的工具会被执行器采纳）。</summary>
public IReadOnlyList<string>? LoadedToolIds { get; set; }
```

`ToolCapabilityKeys` 增加：

```csharp
/// <summary>允许工具通过 ToolResult.LoadedToolIds 声明本执行内已加载（"true"/"false"，默认不可）。</summary>
public const string ExposureLoad = "exposure.load";
```

- [ ] **Step 4: ToolSearchTool 改为声明式**

- 类上加 `[ToolCapability(ToolCapabilityKeys.ExposureLoad, "true")]`。
- 删除 `context.ToolScope?.TryLoad(hitIds);`。
- 结果设置：`if (detail == "full" && discovered && hitIds.Count > 0) result.LoadedToolIds = hitIds;`
- 文案分支改为按"宿主是否支持"：`context.ToolScope is not null` → "已加载：本次执行内可直接调用；下一次执行起自动内联"；否则 → "已登记：下一次执行起可直接调用（本次执行内暂不可调用）"。
  （注意：`Failure`/`Success` 返回后补属性，不要改 `Success` 签名。）

- [ ] **Step 5: 执行器统一应用 + 审计**

在 `ExecuteSingleToolCallAsync` 中，`result = await _tools.ExecuteAsync(...)` 之后调用（**仅在成功时应用**，失败不得解锁）：

```csharp
if (result.Success)
    ApplyDeclaredToolLoads(name, result, toolScope);
```

实现（放同类私有方法）：

```csharp
private void ApplyDeclaredToolLoads(string toolName, ToolResult result, IExecutionToolScope toolScope)
{
    if (result.LoadedToolIds is not { Count: > 0 }) return;
    var tool = _tools.GetTool(toolName);
    if (tool?.Capabilities is null || !tool.Capabilities.ContainsKey(ToolCapabilityKeys.ExposureLoad))
    {
        _logger.LogWarning("[AgentExecutor] 工具声明加载但未声明 {Key} 能力，已忽略: {ToolName}",
            ToolCapabilityKeys.ExposureLoad, toolName);
        return;
    }

    var added = toolScope.TryLoad(result.LoadedToolIds);
    if (added <= 0) return;

    result.Metadata["exposure.loaded"] = result.LoadedToolIds.ToArray();
    _logger.LogInformation("[AgentExecutor] 工具声明加载已生效: {ToolName}, Count={Count}, Loaded={Loaded}",
        toolName, added, result.LoadedToolIds);
}
```

- [ ] **Step 6: 写执行器测试（能力键门控 + 跨步放行 + 审计）**

在 `AgentExecutorToolVisibilityTests` 增加两个工具替身：
- `DeclaringSearchTool`：`Id = tool_search`，声明 `Capabilities => new Dictionary<string,string>{ [ToolCapabilityKeys.ExposureLoad] = "true" }`，执行时 `result.LoadedToolIds = ["hidden_tool"]`。
- `UndeclaredSearchTool`：同上但 `Capabilities => null`。

同时把既有 `LoadingSearchTool`（直接 `TryLoad`）替换为 `DeclaringSearchTool`，让"同执行检索后放行"用例覆盖真实链路（声明 → 执行器应用）。

测试断言：
- 声明能力的工具：第二步调用 `hidden_tool` 不被 Rejected；
- 未声明能力的工具：第二步调用 `hidden_tool` 被 Rejected；
- 声明成功的 `ToolCallEvent.Metadata["exposure.loaded"]` 存在且包含 `hidden_tool`。

（复用现有 `CreateScriptedLlm` 与 `RunAsync(maxSteps: 3)`。）

- [ ] **Step 7: 运行测试**

Run: `dotnet build tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests && dotnet vstest .../Seeing.Agent.Tools.ToolDiscovery.Tests.dll`
Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest .../Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentExecutorToolVisibilityTests"`
Expected: PASS。

- [ ] **Step 8: Commit**

```bash
git commit -am "feat(tool-exposure): 加载改为 ToolResult 声明式上报，执行器统一放行并审计"
```

---

### Task 3: 单一结算快照（DeferredTools）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionToolScope.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Execution/ExecutionToolScope.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Core/ExecutionToolScopeTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/ToolSearchToolTests.cs`

**Interfaces:**
- Produces: `IExecutionToolScope.DeferredTools : IReadOnlyList<ExposedTool>`
- Consumes: `ExposedTool`（Abstractions.Tools：`Id/Namespace/Signature/Schema/EstimatedTokens/Mode`）

- [ ] **Step 1: 写失败测试（作用域暴露 deferred）**

`ExecutionToolScopeTests`：

```csharp
[Fact]
public void DeferredTools_ShouldExposeSettlementDeferredOnly()
{
    var scope = new ExecutionToolScope(Settlement(
        ("read", ToolExposureMode.Inline),
        ("bash", ToolExposureMode.Deferred)));
    scope.DeferredTools.Select(t => t.Id).Should().Equal("bash");
}
```

`ToolSearchToolTests`：

```csharp
[Fact]
public async Task Corpus_ShouldPreferExecutionScopeSnapshot()
{
    var scope = new FakeScopeWithDeferred(Entry("from_scope"));
    var ctx = new ToolContext { SessionId = "s1", Agent = new AgentDefinition { Name = "build" }, ToolScope = scope };
    var tool = CreateTool(Entry("from_resolver"));
    var result = await tool.ExecuteAsync(Args(new { }), ctx);
    result.Output.Should().Contain("from_scope").And.NotContain("from_resolver");
}
```

- [ ] **Step 2: 运行并确认失败**

Expected: 编译失败（`DeferredTools` 不存在）。

- [ ] **Step 3: 实现作用域 deferred 快照**

- 接口加：`IReadOnlyList<ExposedTool> DeferredTools { get; }`（`using Seeing.Agent.Abstractions.Tools;`）。
- 实现构造时收集 `settlement.Tools.Where(t => t.Mode == ToolExposureMode.Deferred).ToArray()`，属性返回该数组（不可变）。

- [ ] **Step 4: ToolSearchTool 改用快照**

```csharp
var settlement = context.ToolScope is null
    ? await _resolver.ResolveAsync(context.SessionId, context.Agent!, ct).ConfigureAwait(false)
    : null;
var corpus = context.ToolScope?.DeferredTools ?? settlement!.DeferredTools;
```

`GetFullDefinition` 改为接收 `ExposedTool entry`，直接 `JsonSerializer.Serialize(entry.Schema)`。

- [ ] **Step 5: 运行测试**

Run: `dotnet vstest .../Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionToolScopeTests"`
Run: `dotnet vstest .../Seeing.Agent.Tools.ToolDiscovery.Tests.dll`
Expected: PASS（含原有"无作用域回退 resolver"用例）。

- [ ] **Step 6: Commit**

```bash
git commit -am "refactor(tool-exposure): tool_search 语料改用执行级冻结快照"
```

---

### Task 4: 会话暴露状态单一写入出口（Scheduler 提升）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Execution/ISessionToolExposureWriter.cs`
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/SessionToolExposureWriter.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/HostingServiceCollectionExtensions.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:445-455`
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/AgentJob.cs:386-415`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Execution/SessionToolExposureWriterTests.cs`（新建）
- Test: `tests/spine/Seeing.Agent.Invariants.Tests/Invariants/ToolViewSingleSourceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface ISessionToolExposureWriter
  {
      void PromotePending(SessionData session);
      Task<bool> PromotePendingAsync(string sessionId, CancellationToken ct = default);
      void Commit(SessionData session, IReadOnlyList<string>? discovered, bool escalated,
                  int schemaTokens, int catalogTokens);
  }
  ```

- [ ] **Step 1: 写失败测试**

`SessionToolExposureWriterTests`：
- `PromotePending` 把 Pending 移入 Discovered；
- `Commit` 写入度量与升级位；
- `PromotePendingAsync("missing")` 返回 `false` 且不抛。

```csharp
[Fact]
public async Task PromotePendingAsync_MissingSession_ShouldReturnFalse_NotThrow()
{
    var manager = new Mock<ISessionManager>();
    manager.Setup(m => m.Get(It.IsAny<string>())).Returns((SessionData?)null);
    manager.Setup(m => m.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync((SessionData?)null);
    var writer = new SessionToolExposureWriter(manager.Object, NullLogger<SessionToolExposureWriter>.Instance);
    (await writer.PromotePendingAsync("missing", TestContext.Current.CancellationToken)).Should().BeFalse();
}
```

- [ ] **Step 2: 运行并确认失败**

Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现端口与注册**

- 接口放 Abstractions `Execution/`，`SessionData` 来自 `Seeing.Session.Core`。
- Hosting 实现：`PromotePending` → `session.ToolExposure.PromotePending()`；`Commit` → `session.ToolExposure.Commit(...)`；`PromotePendingAsync` → `Get ?? await LoadAsync`，null 时返回 false。
- `HostingServiceCollectionExtensions` 注册：`services.AddSingleton<ISessionToolExposureWriter, SessionToolExposureWriter>();`

- [ ] **Step 4: 主链接线**

`ExecutionRunner.RunOnceAsync` 在 scope 创建处解析 writer：

```csharp
var exposureWriter = scope.ServiceProvider.GetRequiredService<ISessionToolExposureWriter>();
```

把 `session.ToolExposure.PromotePending()` / `session.ToolExposure.Commit(...)` 改为 `exposureWriter.PromotePending(session)` / `exposureWriter.Commit(session, ...)`。

> 注意：`ExecutionRunner` 的测试夹具若自建 `ServiceCollection`，必须注册 `ISessionToolExposureWriter`；优先复用 `AddSeeingHosting` 的注册，避免逐个补。

- [ ] **Step 5: Scheduler 接线**

`AgentJob.ResolveToolSchemasAsync` 在拿 resolver 之前：

```csharp
var writer = services.GetService<ISessionToolExposureWriter>();
if (writer is not null && !await writer.PromotePendingAsync(sessionId, cancellationToken).ConfigureAwait(false))
    logger.LogDebug("Scheduler 轮首提升发现：会话不可用，跳过");
```

保持"会话不可用/解析失败 → 空 schema 继续"的既有语义。

- [ ] **Step 6: 更新不变量允许清单**

`ToolViewSingleSourceTests.Session_Exposure_State_Should_Only_Be_Written_At_ExecutionRunner`：
- 允许清单加入 `src/hosting/Seeing.Agent.Hosting/Execution/SessionToolExposureWriter.cs`；
- 从清单移除 `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`（它不再直接调用 `ToolExposure.*`）。

- [ ] **Step 7: 运行测试**

Run: `dotnet build tests/hosting/Seeing.Agent.Hosting.Tests && dotnet vstest .../Seeing.Agent.Hosting.Tests.dll`
Run: `dotnet build tests/spine/Seeing.Agent.Invariants.Tests && dotnet vstest .../Seeing.Agent.Invariants.Tests.dll`
Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest .../Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionRunner"`
Expected: PASS。

- [ ] **Step 8: Commit**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Execution/ISessionToolExposureWriter.cs \
        src/hosting/Seeing.Agent.Hosting/Execution/SessionToolExposureWriter.cs
git commit -am "refactor(tool-exposure): 暴露状态写入收敛到 ISessionToolExposureWriter，Scheduler 补提升"
```

---

### Task 5: `IToolManager` 契约补齐 + 兼容别名

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/IToolManager.cs`
- Modify: `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs`（对齐默认参数）
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/ISessionToolSchemaResolver.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/SessionToolSchemaResolutionTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  Task<ToolResult> ExecuteAsync(
      ToolCall toolCall,
      string sessionId = "",
      CancellationToken cancellationToken = default,
      Func<IMessageEvent, ValueTask>? emitAsync = null,
      IPermissionAuthorizer? permissionAuthorizer = null,
      AgentDefinition? agent = null,
      IExecutionToolScope? toolScope = null);
  ```
- Produces: `[Obsolete] bool IsCallable(string toolId)` → `SendsSchema(toolId)`。

- [ ] **Step 1: 写失败测试**

`SessionToolSchemaResolutionTests` 增加：

```csharp
[Fact]
public void IsCallable_ShouldForwardToSendsSchema_AsObsoleteAlias()
{
#pragma warning disable CS0618
    var resolution = Resolution();
    resolution.IsCallable("inline_tool").Should().BeTrue();
    resolution.IsCallable("deferred_tool").Should().BeFalse();
#pragma warning restore CS0618
}
```

- [ ] **Step 2: 运行并确认失败**

Expected: 编译失败（`IsCallable` 不存在）。

- [ ] **Step 3: 实现**

- `IToolManager.ExecuteAsync` 追加 `AgentDefinition? agent = null, IExecutionToolScope? toolScope = null`；`ToolManager` 实现签名对齐（含默认值）。
- `SessionToolSchemaResolution` 增加：

```csharp
[Obsolete("请改用 SendsSchema；执行层可调用性见 IExecutionToolScope")]
public bool IsCallable(string toolId) => SendsSchema(toolId);
```

- [ ] **Step 4: 运行测试与全量编译**

Run: `dotnet build Seeing.Agent.slnx`
Run: `dotnet vstest .../Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionToolSchemaResolutionTests"`
Expected: 0 error，PASS。

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(abstractions): IToolManager 补齐执行作用域参数；IsCallable 兼容转发"
```

---

### Task 6: 文案统一 + 文档

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/ToolSearchTool.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`（拒绝文案）
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/IToolDiscoveryStore.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/SessionToolDiscoveryStore.cs`
- Modify: `src/primitives/Seeing.Session/Core/SessionToolExposureState.cs`（注释）
- Modify: `docs/architecture/19-tool-context-budgeting-release-notes.md`
- Modify: `docs/architecture/{01,02,03,13,README}.md`
- Modify: `AGENTS.md`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/ToolExposure/ToolExposureEngineTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/ToolSearchToolTests.cs`

- [ ] **Step 1: 写失败测试（文案）**

`ToolExposureEngineTests`：目录开场白包含"本次执行内即可直接调用"与"未展示"，不含"下一轮"。
`ToolSearchToolTests`：`detail` 参数 description 与结果文案不含"下一轮"；含"本次执行内"。

```csharp
[Fact]
public void CatalogIntro_ShouldNotPromiseNextTurn()
{
    var tools = new[] { Schema("read"), Schema("tool_search") };
    var result = ToolExposureEngine.Compute(Input(tools, Config(ToolExposurePolicyMode.Tiered)), Counter);
    result.CatalogText.Should().NotContain("下一轮").And.Contain("本次执行内");
}
```

- [ ] **Step 2: 运行并确认失败**

Expected: 断言失败（仍含"下一轮"）。

- [ ] **Step 3: 统一文案**

- `ToolSearchTool` 类注释与 `detail` description：`full` = "登记本执行内可调用 + 返回完整定义（下一次执行起自动内联，每页最多 3 条）"。
- `ToolExposureEngine` 开场白：
  "以下工具未内联进本次请求的 schema，**未加载前**不能直接调用。先调用 tool_search 检索……命中后**本次执行内即可直接调用**，下一次执行起自动进入 tools[]。目录只展示部分条目，未展示的工具同样存在、同样可检索，不代表缺失。"
- `AgentExecutor` 拒绝文案：`命中后本次执行内即可直接调用`。
- 三个发现态文件的注释改为"登记为下一次执行起内联；本次执行内经执行作用域放行"。
- 文档：删除 `docs/19` 中未验证的"降幅 ≥60% / ≥85%"数字，改为"按 baseline.md 联调实测后回填"；同步 01/02/03/13/README 的"下一轮生效"表述；`AGENTS.md` 删除不存在的 `IToolSearchProvider` 路径引用。

- [ ] **Step 4: 运行测试**

Run: `dotnet vstest .../Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolExposure"`
Run: `dotnet vstest .../Seeing.Agent.Tools.ToolDiscovery.Tests.dll`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git commit -am "docs(tool-exposure): 统一为本次执行内可调用/下一次执行起内联，清理未验证收益数字"
```

---

### Task 7: 全量回归 + 手工联调清单

**Files:**
- Create: `docs/architecture/20-tool-search-provider-smoke-checklist.md`

- [ ] **Step 1: 全量编译**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 error。

- [ ] **Step 2: 全量测试**

Run:
```bash
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.ToolDiscovery.Tests.dll
```
Expected: 全绿（若 `UnifiedConfigManagerSaveSectionsTests.ConcurrentSaves...` 偶发 `UnauthorizedAccessException`，单独复跑确认）。

- [ ] **Step 3: 写手工联调清单**

`docs/architecture/20-*.md` 内容至少包含：
1. 固定 `PolicyMode=Tiered`，确认 `tools[]` 含默认钉选集（19 + `tool_search`）；
2. 提示模型"先 `tool_search detail=full` 再调用命中工具"；
3. 断言：两次请求 `tools[]` 相同；第二步 assistant 出现命中工具名；`ToolCallComplete` 非 Rejected；无 `unknown function` 错误；
4. 再发一条消息，确认命中工具已进入 `tools[]`；
5. 失败判定与 Plan B（轮内重算 schema）触发条件。

- [ ] **Step 4: Commit**

```bash
git add docs/architecture/20-tool-search-provider-smoke-checklist.md
git commit -m "docs(tool-exposure): 补真实 provider 联调清单"
```

---

## 自检结果

- **Spec 覆盖**：D1/D2 钉选 → Task 1；D3 声明式加载 → Task 2；D4 单一快照 → Task 3；D5 写出口 → Task 4；D6 契约 → Task 5；D7 兼容 → Task 5；D8 文案 → Task 6；D9 收益数字 → Task 6；D10 破坏性清单 → Task 6 文档；测试计划 → 各任务 + Task 7。
- **Review Focus**：5 条均在 Task 1/2/3 的测试里落地。
- **类型一致性**：`DeferredTools`、`LoadedToolIds`、`ExposureLoad`、`ISessionToolExposureWriter`、`DefaultPinnedTools` 在首次定义处与后续引用处同名同签名。
