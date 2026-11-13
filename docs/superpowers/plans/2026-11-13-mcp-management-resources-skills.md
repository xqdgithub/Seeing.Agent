# MCP 管理 / Resources / Skills / Prompts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **本计划按波次并行**：同波次内无依赖的 Task 必须并行派发。

**Goal:** 让 agent 能管理 MCP server，能按需读 Resources，把 MCP Skills 并进现有技能目录，把 MCP Prompts 接到用户斜杠命令；并拆出 `IMcpSession`，修掉工作区切换写错 `mcp.json` 的洞。

**Architecture:** `IMcpManager` 继续编排配置与生命周期；每连接一个 `IMcpSession`（Stdio / HTTP，OAuth 装饰在 HTTP 外）。管理与 Resource 走 `ProvidedTools`。Skills 经 Abstractions 贡献端口由 Skills 模块并进快照，禁止 `RegisterAsync`。Prompts 经 `ICommandRegistry`，结束本轮后只投递一条 `InboxDelivery.Wake`。

**Tech Stack:** .NET 10、`ModelContextProtocol.Core` 2.2.0、xUnit v3、Moq、FluentAssertions、VSTest。

规格：`docs/superpowers/specs/2026-11-13-mcp-management-resources-skills-design.md`

## Global Constraints

- 能力包只依赖 Abstractions + 原语（+ Tools.Support）。`Seeing.Agent.Mcp` 禁止引用 `Seeing.Agent.Core`。
- 远端工具 id 仍为 `mcp__{server}__{tool}`。管理 / Resource 工具 id 用单下划线：`mcp_list_servers` 等。
- 权限 kind 是字符串，不新增 `PermissionKind` 枚举：`mcp.manage.read` / `mcp.manage.write` / `mcp.manage.lifecycle` / `mcp.resource.read`。grant 按字符串精确匹配；`mcp.resource.read` 的 Resource 为 `server|uri`。
- 工作区：`IWorkspaceApplyParticipant`；`OnApplyingAsync` 禁止 Save；未 `IsResolved` 拒绝项目级写入。
- Skills：禁止 `ISkillCatalog.RegisterAsync`；贡献端口只由 Skills 模块消费。
- Prompts：`RemoveCommandMessage = true`，`ShouldContinue = false`，再 `DeliverAsync(Wake, User)`；禁止 `AddMessage`。
- 测试禁止固定等待。VSTest：

```bash
dotnet build tests/capabilities/Seeing.Agent.Mcp.Tests/Seeing.Agent.Mcp.Tests.csproj
dotnet vstest tests/capabilities/Seeing.Agent.Mcp.Tests/bin/Debug/net10.0/Seeing.Agent.Mcp.Tests.dll --TestCaseFilter:"FullyQualifiedName~ClassName"
```

- Skills 相关测试另跑 `tests/capabilities/Seeing.Agent.Skills.Tests`（若有）或在该项目下新建。
- 每个 Task 结束 commit，信息中文：`feat(mcp): …` / `test(mcp): …` / `refactor(mcp): …`。
- 不写 `docs/architecture` release note（除非用户另要）。

---

## 并行波次（执行顺序）

```
Wave 0（串行，阻塞一切）
  Task 1  IMcpSession 契约 + capabilities DTO + McpServerStatus 扩展

Wave 1（Task 1 合并后并行）
  Task 2  Session 实现外置 + Manager 改用 Session + 回归
  Task 3  技能贡献端口（Abstractions）+ Skills 模块并进快照（可与 Task 2 并行）

Wave 2（Task 2 合并后并行；Task 5 还依赖 Task 3）
  Task 4  管理工具 + 权限门 + ProvidedTools + 工作区参与者
  Task 5  Resource 工具 + mcp.resource.read
  Task 6  MCP 技能贡献实现 + 加载回源（依赖 Task 2 + Task 3 + Task 5 的 read 能力；可与 Task 4 后半重叠）
  Task 7  Prompt 斜杠命令（依赖 Task 2 的 Prompts API；可与 Task 4/5 并行）
```

**并行派发规则：** 同一 Wave 内无箭头依赖的 Task 同时开 subagent。Task 6 等到 Task 3 的端口与 Task 2 的 SkillsExt/Resources 可读后再开；若 Resource 读已在 Session 上可用，不必等 Task 5 的 agent 工具完成。

---

## 文件结构

### 新建

| 路径 | 职责 |
|------|------|
| `src/abstractions/.../Mcp/IMcpSession.cs` | 单连接协议面 |
| `src/abstractions/.../Mcp/McpServerCapabilitiesInfo.cs` | capabilities 摘要 DTO |
| `src/abstractions/.../Mcp/McpResourceInfo.cs` 等 | Resource / Prompt / SkillExt 只读模型 |
| `src/abstractions/.../Skills/ISkillContributionSource.cs` | 外部技能贡献端口 |
| `src/abstractions/.../Skills/SkillContributionModels.cs` | Descriptor / Content / Attachment |
| `src/capabilities/Seeing.Agent.Mcp/Session/*.cs` | Stdio/Http Session；OAuth 装饰 |
| `src/capabilities/Seeing.Agent.Mcp/Tools/Manage/*.cs` | 管理工具 |
| `src/capabilities/Seeing.Agent.Mcp/Tools/Resources/*.cs` | Resource 工具 |
| `src/capabilities/Seeing.Agent.Mcp/Tools/McpManagePermissionGate.cs` | manage/resource 权限门 |
| `src/capabilities/Seeing.Agent.Mcp/Workspace/McpWorkspaceParticipant.cs` | 工作区参与者 |
| `src/capabilities/Seeing.Agent.Mcp/Skills/McpSkillContributionSource.cs` | MCP → 贡献端口 |
| `src/capabilities/Seeing.Agent.Mcp/Commands/McpPromptCommand*.cs` | Prompt 斜杠 |
| `src/capabilities/Seeing.Agent.Skills/Catalog/SkillContributionAggregator.cs` | 登记源、并进 Publish |
| 对应 `tests/capabilities/Seeing.Agent.Mcp.Tests/...` | 各面单测 |

### 修改

| 路径 | 改动 |
|------|------|
| `McpServerStatus.cs` | 增 Capabilities / 可选计数；同步 `Clone` |
| `McpClientManager.cs` | 持 Session；删公开窄 Wrapper 或内化 |
| `McpConnectionCoordinator.cs` / Factories | 产出 `IMcpSession` |
| `McpModule.cs` | ProvidedTools、工具 DI、WorkspaceParticipant、Skill 源、Prompt 命令 |
| `SkillCatalogCore.cs` | Publish 合并贡献源；加载回调 |
| `SkillTool` / 加载路径 | 贡献条目回源，不走 `file:///` |
| `DynamicSkillCommandSync.cs` | 与内置命令重名则跳过（若尚无） |
| `McpModuleTests` | `ProvidedTools` 不再断言 empty |

### 锁定字符串

| 项 | 值 |
|----|-----|
| 技能目录名 / 命令 token | `mcp_{serverId}_{skillName}`，`serverId`/`skillName` 只保留 `[A-Za-z0-9_-]`，其它变 `_`；若与已有技能或内置命令冲突则跳过并 Warning |
| Prompt 命令 | 每条登记为 `mcp-prompt-{serverId}-{promptName}`（同样净化）；入口说明可在 `/mcp` 页；与 `/mcp`、`/mcp-auth` 不冲突 |
| Resource 记忆键 | `{server}\|{uri}`（字面竖线） |

---

### Task 1: IMcpSession 契约与状态扩展

**Wave:** 0  
**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Mcp/IMcpSession.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Mcp/McpServerCapabilitiesInfo.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Mcp/McpResourceModels.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Mcp/McpPromptModels.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Mcp/McpSkillExtModels.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Mcp/McpServerStatus.cs`
- Modify: `src/capabilities/Seeing.Agent.Mcp/Core/McpServerStatusBuilder.cs`（若存在字段列表）
- Test: `tests/capabilities/Seeing.Agent.Mcp.Tests/McpServerStatusCapabilitiesTests.cs`

**Interfaces:**
- Produces: 下列契约（命名空间 `Seeing.Agent.Abstractions.Mcp`）。路径 `src/abstractions/Seeing.Agent.Abstractions/Mcp/`。

```csharp
public interface IMcpSession : IAsyncDisposable
{
    string ServerName { get; }
    McpServerCapabilitiesInfo Capabilities { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<McpSessionToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    Task<McpResourceListResult> ListResourcesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);
    Task<McpResourceReadResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default);
    Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<McpPromptListResult> ListPromptsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);
    Task<McpPromptGetResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default);

    Task<McpSkillExtListResult> ListSkillsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);
    Task<McpSkillExtGetResult> GetSkillAsync(
        string uri,
        CancellationToken cancellationToken = default);
}

public sealed record McpServerCapabilitiesInfo(
    bool Tools,
    bool Resources,
    bool ResourcesSubscribe,
    bool ResourcesListChanged,
    bool Prompts,
    bool PromptsListChanged,
    bool SkillsExtension,
    bool SkillsDirectoryRead);

// List/Read 结果类型：含 Items/Contents、NextCursor、NotSupported、ErrorMessage
// SkillExt 条目含 Uri、Name、Description、FrontmatterJson（可选）
```

无对应 capability 时方法返回 `NotSupported = true`，**禁止**抛未处理 SDK 异常冒充成功。

`McpServerStatus` 新增：`McpServerCapabilitiesInfo? Capabilities`，`int? ResourceCount`，`int? PromptCount`，`int? SkillCount`。构造函数与 `Clone()` 必须带上。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Clone_Preserves_Capabilities_And_Optional_Counts()
{
    var caps = new McpServerCapabilitiesInfo(
        Tools: true, Resources: true, ResourcesSubscribe: false, ResourcesListChanged: false,
        Prompts: true, PromptsListChanged: false, SkillsExtension: true, SkillsDirectoryRead: false);
    var status = new McpServerStatus(
        "demo", McpConnectionState.Connected, config: null, McpServerPriority.Normal,
        toolCount: 2, toolNames: new[] { "a", "b" },
        createdAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        lastConnectedAt: null, lastErrorAt: null, lastError: null,
        reconnectAttempts: 0, activePolicy: null,
        capabilities: caps, resourceCount: 3, promptCount: 1, skillCount: 2);

    var clone = status.Clone();
    clone.Capabilities.Should().Be(caps);
    clone.ResourceCount.Should().Be(3);
    clone.PromptCount.Should().Be(1);
    clone.SkillCount.Should().Be(2);
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet build tests/capabilities/Seeing.Agent.Mcp.Tests/Seeing.Agent.Mcp.Tests.csproj
dotnet vstest tests/capabilities/Seeing.Agent.Mcp.Tests/bin/Debug/net10.0/Seeing.Agent.Mcp.Tests.dll --TestCaseFilter:"FullyQualifiedName~McpServerStatusCapabilitiesTests"
```

Expected: 编译失败或断言相关失败（构造函数尚无新参数）。

- [ ] **Step 3: 落地契约与状态字段**

实现上述文件；更新所有 `new McpServerStatus(...)` / `McpServerStatusBuilder` 调用点以通过编译（新参数默认 `null`）。

- [ ] **Step 4: 测试通过并 commit**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Mcp tests/capabilities/Seeing.Agent.Mcp.Tests src/capabilities/Seeing.Agent.Mcp
git commit -m "$(cat <<'EOF'
feat(mcp): 增加 IMcpSession 契约与状态 capabilities 字段

为会话面拆分与 UI/工具共用能力摘要铺路。
EOF
)"
```

---

### Task 2: Session 实现外置 + Manager 接线

**Wave:** 1（并行 A）  
**Depends:** Task 1  
**Files:**
- Create: `src/capabilities/Seeing.Agent.Mcp/Session/StdioMcpSession.cs`
- Create: `src/capabilities/Seeing.Agent.Mcp/Session/HttpMcpSession.cs`
- Create: `src/capabilities/Seeing.Agent.Mcp/Session/OAuthHttpMcpSession.cs`（装饰 Http；或迁改现有 `OAuthHttpClientWrapper`）
- Modify: `Factory/*WrapperFactory.cs` → 工厂返回 `IMcpSession`
- Modify: `Management/McpConnectionCoordinator.cs`
- Modify: `McpClientManager.cs`（会话表；连接后填 Capabilities；EmbeddedResource 输出 URI 提示）
- Delete or internalize: `IMcpClientWrapper` 公开面
- Test: `tests/capabilities/Seeing.Agent.Mcp.Tests/Session/McpSessionCapabilitiesGateTests.cs`
- Test: 现有 `tests/spine/Seeing.Agent.Tests/MCP/*` 与 `Seeing.Agent.Mcp.Tests` 回归

**Interfaces:**
- Consumes: `IMcpSession`、`McpServerCapabilitiesInfo`
- Produces: 工厂 `IMcpSession Create(...)`；Manager 在 Connected 后 `status.Capabilities` 非空（至少 Tools 位与 SDK 一致）

- [ ] **Step 1: 写能力门控单测（假 Session）**

用测试替身：`Capabilities.Resources == false` 时 `ListResourcesAsync` 返回 `NotSupported`；为 true 时返回条目。可先测纯函数门控辅助，再测真实 Session 包装。

```csharp
[Fact]
public async Task ListResources_When_NotDeclared_Returns_NotSupported()
{
    var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
        true, false, false, false, false, false, false, false));
    var result = await session.ListResourcesAsync();
    result.NotSupported.Should().BeTrue();
}
```

- [ ] **Step 2: 迁移传输实现**

把 `StdioMcpClientWrapper` / `HttpMcpClientWrapper` 逻辑迁到 Session 文件。Connect 后读 `McpClient.ServerCapabilities`（及 Extensions 中 `io.modelcontextprotocol/skills`）填 `Capabilities`。Skills 方法：仅当 SkillsExtension 时发 JSON-RPC `skills/list` / `skills/get`；否则 NotSupported。

OAuth：保持「外层装饰 → 内层 Http Session」，401 刷新重试语义不变。

- [ ] **Step 3: Coordinator / Manager 改持 `IMcpSession`**

连接成功：`ListToolsAsync` → 注册动态工具；写入 status.Capabilities；可选先不填 ResourceCount（Task 5/6 可填）。

EmbeddedResourceBlock：输出 `uri` + 提示 `mcp_read_resource`（工具尚未挂载时文案仍可写）。

- [ ] **Step 4: 全量 MCP 相关测试**

```bash
dotnet build tests/capabilities/Seeing.Agent.Mcp.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Mcp.Tests/bin/Debug/net10.0/Seeing.Agent.Mcp.Tests.dll
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Mcp"
```

- [ ] **Step 5: Commit**

```bash
git commit -m "$(cat <<'EOF'
refactor(mcp): 外置 IMcpSession 并按 capabilities 门控

去掉窄 Wrapper 扩展点，OAuth 仍装饰 HTTP 会话。
EOF
)"
```

---

### Task 3: 技能贡献端口 + Skills 模块并进快照

**Wave:** 1（并行 B）  
**Depends:** Task 1（无硬依赖 Session；可与 Task 2 并行）  
**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillContributionSource.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Skills/SkillContributionModels.cs`
- Create: `src/capabilities/Seeing.Agent.Skills/Catalog/SkillContributionRegistry.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogCore.cs`
- Modify: Skills DI 注册
- Modify: 技能加载路径（`SkillTool` / 命令加载）以支持回源
- Modify: `DynamicSkillCommandSync.cs`（重名跳过）
- Test: `tests/capabilities/Seeing.Agent.Skills.Tests/SkillContributionCatalogTests.cs`（若无 Skills.Tests 则加到合适测试项目）

**Interfaces:**

```csharp
namespace Seeing.Agent.Abstractions.Skills;

public interface ISkillContributionSource
{
    /// <summary>稳定前缀，如 mcp。</summary>
    string SourceKind { get; }
    Task<IReadOnlyList<SkillContributionDescriptor>> ListAsync(CancellationToken ct = default);
    Task<SkillContributionContent?> LoadAsync(string key, CancellationToken ct = default);
}

public sealed record SkillContributionDescriptor(
    string Key,
    string Description,
    string ProviderId);

public sealed record SkillContributionContent(
    string MarkdownBody,
    IReadOnlyList<SkillContributionAttachment> Attachments);

public sealed record SkillContributionAttachment(
    string RelativePath,
    string Uri,
    string? MimeType);

public interface ISkillContributionRegistry
{
    void Register(ISkillContributionSource source);
    void Unregister(ISkillContributionSource source);
    IReadOnlyList<ISkillContributionSource> Sources { get; }
}
```

Publish 规则：
- 贡献条目进入快照时 `SkillSource` **不要**用会遮蔽文件的 `Registered` 赢者逻辑去盖本地同名。贡献 `Key` 已与本地名隔离（`mcp_…` 前缀）。可用新枚举值 `SkillSource.Contribution`，或复用 `Registered` 但 **Key 必须已限定** 且 Publish 时若与文件同名则 **丢弃贡献并记 Conflict/Warning**（不得盖文件）。**选定：新增 `SkillSource.Contribution`。**
- `SkillInfo.Content` 发现期可空或短描述；加载时 `LoadAsync` 填正文。
- `DirectoryPath` 对贡献为空串；附件不走 `GetSkillFiles`。

- [ ] **Step 1: 失败测试 — 贡献进入快照且不盖本地**

假源贡献 `mcp_s1_demo`；磁盘同名 `demo` 仍在；贡献与 `demo` 共存。假源若贡献键 `demo`（冲突），则文件胜出、贡献被记录为 conflict/skip。

- [ ] **Step 2: 实现 Registry + Publish 合并 + 加载回源**

- [ ] **Step 3: DynamicSkillCommandSync — `HasCommand` 已存在则跳过**

- [ ] **Step 4: 测试通过 + commit**

```bash
git commit -m "$(cat <<'EOF'
feat(skills): 外部技能贡献端口并进目录快照

发现期只合并元数据，加载期回源；不走 RegisterAsync。
EOF
)"
```

---

### Task 4: 管理工具 + 工作区参与者

**Wave:** 2（并行）  
**Depends:** Task 2（Manager/Session 稳定）  
**Files:**
- Create: `Tools/McpManagePermissionGate.cs`
- Create: `Tools/Manage/McpListServersTool.cs` …（见下表）
- Create: `Workspace/McpWorkspaceParticipant.cs`
- Modify: `McpModule.cs` ProvidedTools + DI + participant
- Modify: `McpClientManager.AddServerAsync` / Import：尊重工具传入的 level；增加「未 resolved 拒项目级」闸（经 `IWorkspaceProvider`）
- Modify: `AddServerAsync` 的 connect 覆盖 `AutoStartOnAdd`（工具参数 `connect`：`true`/`false`/省略=跟随策略）
- Test: `McpManageToolsTests.cs`、`McpWorkspaceParticipantTests.cs`
- Update: `McpModuleTests` 不再要求 ProvidedTools empty

**工具表（全部实现，勿只做 list）：**

| Id | Manager API | kind | 要点 |
|----|-------------|------|------|
| `mcp_list_servers` | GetAllStatus | read | 含 capabilities 摘要 |
| `mcp_get_server` | GetStatus/GetConfig | read | |
| `mcp_add_server` | AddServerAsync | write | 必填 level；`connect` 三态 |
| `mcp_update_server` | UpdateConfigAsync | write | |
| `mcp_remove_server` | RemoveServerAsync | write | |
| `mcp_enable_server` | EnableServerAsync | write | |
| `mcp_disable_server` | DisableServerAsync | write | |
| `mcp_pause_server` | PauseServerAsync | lifecycle | |
| `mcp_resume_server` | ResumeServerAsync | lifecycle | |
| `mcp_reconnect_server` | ReconnectServerAsync | lifecycle | |
| `mcp_import_servers` | ImportFromJsonAsync | write | 必填 level |

权限门：对齐 `McpToolPermissionGate`，kind 换上表；失败 Fail-closed。

工作区参与者：

```csharp
public sealed class McpWorkspaceParticipant : IWorkspaceApplyParticipant
{
    public string ComponentId => "mcp";
    // OnApplyingAsync: 断开所有 ConfigLevel.Project 会话；设置 _suppressProjectSave
    // OnAppliedAsync: 清除 suppress；Reload 用户级+项目级；重连策略与现网 Initialize 一致
}
```

`SaveConfigAsync(Project)` 在 suppress 或 `!IsResolved` 时抛或返回 Failed，且 **不写盘**。

- [ ] **Step 1: 测试 — kind 与 execute grant 不互通；未 resolved 拒项目写入；Applying 不 Save**

- [ ] **Step 2: 实现门、工具、参与者、Module 挂载**

对标 `CronListTool`：`ToolBase` + DI singleton + Activate `RegisterToolAsync`。

- [ ] **Step 3: VSTest 过滤 Manage/Workspace + ModuleTests**

- [ ] **Step 4: Commit**

```bash
git commit -m "$(cat <<'EOF'
feat(mcp): 管理工具与工作区参与者

agent 可持久化管理 server；切换工作区不再写错项目级 mcp.json。
EOF
)"
```

---

### Task 5: Resource 工具

**Wave:** 2（并行）  
**Depends:** Task 2  
**Files:**
- Create: `Tools/Resources/McpListResourcesTool.cs`
- Create: `Tools/Resources/McpReadResourceTool.cs`
- Create: `Tools/Resources/McpListResourceTemplatesTool.cs`
- Modify: `McpManagePermissionGate` 或独立 `McpResourcePermissionGate`（kind `mcp.resource.read`，Resource=`server|uri`；list 可用 `server|*`）
- Modify: `McpModule.ProvidedTools`
- Test: `McpResourceToolsTests.cs`

- [ ] **Step 1: 测试 — 无 capability → 明确失败；read 权限键为 server|uri；与 mcp.execute grant 分离**

- [ ] **Step 2: 实现三工具**（经 Manager 取 Session；二进制摘要）

- [ ] **Step 3: 测试通过 + commit**

```bash
git commit -m "$(cat <<'EOF'
feat(mcp): Resource 专用 list/read 工具

按 capabilities 门控，权限记忆键为 server|uri。
EOF
)"
```

---

### Task 6: MCP 技能贡献实现

**Wave:** 2（依赖 Task 2+3；Resource read 走 Session，不强制等 Task 5 工具）  
**Files:**
- Create: `Skills/McpSkillContributionSource.cs`
- Modify: `McpModule` 注册到 `ISkillContributionRegistry`；Disconnect/Disable/Deactivate 时源 `ListAsync` 变空并触发 Refresh
- Test: `McpSkillContributionTests.cs`

行为：
- 遍历已连接且 `Capabilities.SkillsExtension` 的 server → `ListSkillsAsync` → Descriptor.Key = `mcp_{server}_{name}`
- `LoadAsync`：`GetSkillAsync` + `ReadResourceAsync` 正文与清单；每次 read 走与 Resource 工具相同的权限门（`mcp.resource.read`）。若加载上下文无 Session 权限，则经可注入的授权回调；无授权器 Fail-closed。
- 与本地或内置命令冲突：不发布该键。

- [ ] **Step 1–4: TDD 假 Session + 假 Registry；commit**

```bash
git commit -m "$(cat <<'EOF'
feat(mcp): MCP Skills 经贡献端口进入技能目录

加载回源 resources/read，不 RegisterAsync。
EOF
)"
```

---

### Task 7: Prompt 斜杠命令

**Wave:** 2（并行）  
**Depends:** Task 2  
**Files:**
- Create: `Commands/McpPromptCommand.cs`（一条 prompt 一个 ICommand 实例，或工厂）
- Create: `Commands/McpPromptCommandSync.cs`（订阅 StatusChanged / 连接生命周期）
- Modify: `McpModule` Activate/Deactivate
- Test: `McpPromptCommandTests.cs`

命令执行：

```csharp
// 伪代码 — 实现必须行为等价
return new CommandResult {
    Success = true,
    RemoveCommandMessage = true,
    ShouldContinue = false,
    Message = "已展开 MCP prompt"
};
// 然后：
await sessionRuntime.DeliverAsync(new InboxMessage {
    TargetSessionId = ctx.SessionId,
    Sender = InboxSender.User, // 按现有用户发送约定
    Body = joinedPromptText,
    Delivery = InboxDelivery.Wake,
    Presentation = InboxPresentation.User
}, ct);
```

`Body`：拼接 `PromptMessage`；assistant 段加前缀；embedded resource 只留 URI + `mcp_read_resource` 提示。

登记名：`mcp-prompt-{server}-{prompt}`；`HasCommand` 则跳过。

- [ ] **Step 1: 测试 — ShouldContinue false；DeliverAsync 恰一次；不 AddMessage**

- [ ] **Step 2: 实现 Sync + Command**

- [ ] **Step 3: 测试 + commit**

```bash
git commit -m "$(cat <<'EOF'
feat(mcp): Prompts 映射为斜杠命令并 Wake 投递

结束本轮后只投递一条用户消息，避免双投递。
EOF
)"
```

---

## Spec 覆盖自检

| 规格章节 | Task |
|----------|------|
| P0 Session 拆分 / OAuth 装饰 / Clone | 1, 2 |
| P0 管理工具 / kind 字符串 | 4 |
| P0 工作区参与者 / ConfigLevel / 未 resolved | 4 |
| P1 Resources 工具 / server\|uri | 5 |
| P2 贡献端口 / 禁止 RegisterAsync / Skills 消费 | 3, 6 |
| P3 Prompt 投递契约 | 7 |
| EmbeddedResource 提示 | 2 |
| 非目标（Sampling 等） | 不实现 |

## Placeholder 扫描

无 TBD。Skill Key 与 Prompt 命令名已在「锁定字符串」写死。
