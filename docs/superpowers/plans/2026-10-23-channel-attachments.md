# 通联出站附件（文件/图片/音视频）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 agent 能通过通用「文本 + 附件」出站模型把工作区文件发到 QQ，抽象渠道无关、无兼容层。

**Architecture:** 契约层新增 `OutboundContent` / `OutboundAttachment` / `ChannelCapabilities`；出站单一路径 `工具 → Sink → OutboundDeliveryService（能力裁决 + 回复锚点）→ IChannelAdapter`；协议帧携带内容与能力；QQ 适配器是唯一媒体实现，删除旧的 `[Image:]`/`[File:]` 文本标记链路。

**Tech Stack:** .NET 10、C# 12/13、xUnit + FluentAssertions + Moq、System.Text.Json、MSBuild（`dotnet build` + `dotnet vstest`）。

**Spec:** `docs/superpowers/specs/2026-10-23-channel-attachments-design.md`

## Global Constraints

- 目标框架 `net10.0`；不新增项目、不新增配置节、不新增 NuGet 包。
- **无兼容层**：不留旧重载、默认接口实现、可选参数默认值、兼容字段；所有调用点一次性改造。
- 依赖方向：契约只进 `Abstractions`；`Comms` 只做编排；工具包只依赖 `Abstractions`；QQ/WeCom 实现在各自 gateway 包。
- `Abstractions` 零实现：DTO 不带业务方法，派生查询只放扩展方法。
- 工具禁止 `File.*` / `Directory.*`，必须经 `IFileSystem`；工具描述与 Schema 遵守 `docs/architecture/04` §9（R1–R9）。
- 注释、日志、错误文案使用简体中文；结构化日志用 `{PropertyName}`。
- 附件路径安全：严格「会话 cwd ∪ 项目根」，不查扩权白名单、不受 `Workspace.RestrictToWorkspace` 影响。
- 测试统一用 `dotnet build <测试项目>` 后 `dotnet vstest <bin/Debug/net10.0/程序集>.dll`（本机 MTP 无法发现测试）。

## 执行模型（并行优先，只在关键点审查）

| 阶段 | 任务 | 并行 | 门禁 |
|---|---|---|---|
| Wave 1 | Task 1（契约 + 协议 + 编译基线） | 串行（所有后续任务的前置） | **Gate 1**：契约与能力链路审查（fresh subagent） |
| Wave 2 | Task 2 / 3 / 4 / 5 / 6 | **并行**（各自 git worktree；文件不相交） | **Gate 2**：Wave 2 合并后一次集成审查（fresh subagent） |
| Wave 3 | Task 7（全量构建 + 全测试 + 残留扫描） | 串行 | **Gate 3**：整支分支终审（fresh subagent） |

并行约定：

1. 每个 Wave 2 任务在自己的 worktree 内实现、构建、测试、提交；合并顺序按任务号。
2. 任务之间**不得修改同一文件**（本计划的文件清单已保证互斥）；若实现中发现需要改他人文件，停手并报告，不要跨任务修改。
3. 每个任务只跑自己负责的测试项目；全量构建与全量测试只在 Task 7 执行（避免并发 MSBuild 文件锁）。
4. 审查只发生在 Gate 1/2/3；任务内部按 TDD 自检，不逐任务派审查。

---

## Review Focus（最可能出问题、且必须由测试钉住的行为）

1. **纯文本发到 `Attachments == null` 的通道**（如 WeCom）：必须正常投递，不能因能力为 null 而 NRE。→ Task 2 测试。
2. **被动额度逐条计数**：文本分块、每个附件、ack、权限卡片、终态回复各自消耗一次；额度耗尽降级为主动消息。→ Task 4 测试。
3. **工作区越界外发**：`Workspace.RestrictToWorkspace=false` 时，附件路径仍必须被拒绝。→ Task 3 测试。
4. **显式目标 + 会话未绑定通道**：返回 `NotInDirectory`（附「请提供 channelId」），不能折叠成参数错误；只给 `channelId` 反而必须是参数错误。→ Task 3 测试。
5. **注册帧缺能力或 `capabilities: null`**：回 `error(invalid_capabilities)`、不注册、不断连接。→ Task 1 测试。

---

## Task 1: 契约、协议与全仓编译基线

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelAttachments.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelCapabilities.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelCapabilitiesExtensions.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelModels.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelPorts.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/IFileSystem.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Permissions/IWorkspacePathGate.cs`
- Modify: `src/capabilities/Seeing.IO.Local/LocalFileSystem.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/WorkspacePathGate.cs`
- Modify: `src/gateway/Seeing.Gateway/Protocol/GatewayWsFrame.cs`
- Modify: `src/gateway/Seeing.Gateway/IGatewayConnection.cs`
- Modify: `src/gateway/Seeing.Gateway/IChannelBridge.cs`
- Modify: `src/gateway/Seeing.Gateway.Client/IGatewayChannelHostClient.cs`
- Modify: `src/gateway/Seeing.Gateway.Client/WebSocketGatewayClient.cs`
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayConnectionManager.cs`
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/RemoteChannelAdapterProxy.cs`
- Modify: `src/gateway/Seeing.Agent.Gateway/Endpoints/GatewayWebSocketHandler.cs`
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayDeliveryNotifier.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelAdapter.cs`（仅能力声明与 `Content` 取文本）
- Modify: `src/gateway/Seeing.Gateway.QQ/Extensions/QQServiceCollectionExtensions.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelBridge.cs`（仅 `Content` 映射 + `Capabilities`）
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComChannelAdapter.cs`（能力声明 + `Content.Text`）
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComChannelBridge.cs`（`Content` 映射 + `Capabilities`）
- Modify: `src/capabilities/Seeing.Agent.Comms/Delivery/ChannelMessageSink.cs`
- Modify: `src/capabilities/Seeing.Agent.Comms/Directories/ChannelDirectoryService.cs`（锚点读写）
- Modify: `src/capabilities/Seeing.Agent.Comms/Scheduling/ChannelDeliveryScheduleDispatcher.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/NoopChannelMessageSink.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/SendMessageTool.cs`（先只改签名，功能在 Task 3）
- Modify: `samples/Seeing.Gateway.ChannelHost/Program.cs`
- Test: `tests/abstractions/Seeing.Agent.Abstractions.Tests/Channels/ChannelCapabilitiesTests.cs`（新建）
- Modify: 测试编译：`tests/abstractions/Seeing.Agent.Abstractions.Tests/Channels/ChannelModelsTests.cs`、`tests/gateway/Seeing.Gateway.Tests/Core/GatewayChannelRoutingTests.cs`、`tests/gateway/Seeing.Gateway.Tests/Protocol/PayloadV2SerializationTests.cs`、`tests/gateway/Seeing.Gateway.Client.Tests/{WebSocketGatewayClientTests,RemoteSessionClientSequentialExchangeTests}.cs`、`tests/capabilities/Seeing.Agent.Comms.Tests/Delivery/{ChannelMessageSinkTests,OutboundDeliveryServiceTests}.cs`、`tests/capabilities/Seeing.Agent.Comms.Tests/Scheduling/ChannelDeliveryScheduleDispatcherTests.cs`、`tests/gateway/Seeing.Agent.Comms.IntegrationTests/{InProcessGroupDeliveryTests.cs,Support/CommsIntegrationFixture.cs}`

**Interfaces:**
- Produces（后续任务按此实现）：
  - `OutboundAttachment { OutboundAttachmentKind Kind; string LocalPath; long SizeBytes; string? FileName }`
  - `OutboundContent { string? Text; IReadOnlyList<OutboundAttachment> Attachments }`
  - `ChannelAttachmentCapabilities { IReadOnlyList<OutboundAttachmentKind> Kinds; long MaxBytes; int MaxPerMessage }`
  - `ConversationAttachmentCapabilities { ConversationKind Kind; ChannelAttachmentCapabilities Capabilities }`
  - `ChannelCapabilities { required bool SupportsReplyAnchor; ChannelAttachmentCapabilities? Attachments; IReadOnlyList<ConversationAttachmentCapabilities>? AttachmentsByConversationKind }`
  - `ChannelCapabilitiesExtensions.Supports(this ChannelAttachmentCapabilities, OutboundAttachmentKind) : bool`
  - `ChannelCapabilitiesExtensions.ResolveAttachments(this ChannelCapabilities, ConversationKind) : ChannelAttachmentCapabilities?`
  - `IChannelMessageSink.SendToUserAsync(string channelId, string userExternalId, OutboundContent content, CancellationToken ct = default) : Task<OutboundDeliveryResult>`
  - `IChannelMessageSink.SendToConversationAsync(DeliveryTarget target, OutboundContent content, CancellationToken ct = default) : Task<OutboundDeliveryResult>`
  - `IChannelTargetResolver.ResolveSessionTargetAsync(string sessionId, CancellationToken ct = default) : Task<ChannelTargetResolution>`
  - `ChannelTargetResolution { DeliveryTarget? Target; OutboundFailureKind? FailureKind; string? Detail }` + `Ok(...)` / `Fail(...)`
  - `IChannelAdapter.Capabilities { get; } : ChannelCapabilities`
  - `IChannelDirectory.ResolveReplyAnchor(ConversationInfo) : string?`
  - `IGatewayConnection.ConnectAsync(string channelId, ChannelCapabilities capabilities, CancellationToken ct = default)`
  - `IGatewayChannelHostClient.ConnectAsync(string channelId, ChannelCapabilities capabilities, CancellationToken ct = default)`
  - `IChannelBridge.Capabilities { get; } : ChannelCapabilities`
  - `IWorkspacePathGate.EnsureWithinWorkspace(string sessionId, string path) : string?`
  - `IFileSystem.GetFileLength(string path) : long`、`IFileSystem.IsDirectory(string path) : bool`

- [ ] **Step 1: 写契约与协议测试（先失败）**

新建 `tests/abstractions/Seeing.Agent.Abstractions.Tests/Channels/ChannelCapabilitiesTests.cs`：

```csharp
[Fact]
public void ResolveAttachments_ShouldFallbackToDefault_WhenKindNotOverridden()
{
    var caps = new ChannelCapabilities
    {
        SupportsReplyAnchor = true,
        Attachments = new() { Kinds = [OutboundAttachmentKind.File], MaxBytes = 10, MaxPerMessage = 2 }
    };

    caps.ResolveAttachments(ConversationKind.Group)!.Kinds.Should().Contain(OutboundAttachmentKind.File);
}

[Fact]
public void ResolveAttachments_ShouldReturnNull_WhenChannelSupportsNoAttachments()
{
    var caps = new ChannelCapabilities { SupportsReplyAnchor = false };
    caps.ResolveAttachments(ConversationKind.Direct).Should().BeNull();
}

[Fact]
public void ResolveAttachments_ShouldUseFirstMatchingOverride()
{
    var guild = new ChannelAttachmentCapabilities { Kinds = [OutboundAttachmentKind.Image], MaxBytes = 5, MaxPerMessage = 1 };
    var caps = new ChannelCapabilities
    {
        SupportsReplyAnchor = true,
        Attachments = new() { Kinds = [OutboundAttachmentKind.File], MaxBytes = 10, MaxPerMessage = 2 },
        AttachmentsByConversationKind = [new() { Kind = ConversationKind.Guild, Capabilities = guild }]
    };

    caps.ResolveAttachments(ConversationKind.Guild).Should().BeSameAs(guild);
}
```

在 `tests/gateway/Seeing.Gateway.Tests/Protocol/PayloadV2SerializationTests.cs` 增加注册帧与出站帧往返：

```csharp
[Fact]
public void ChannelRegisterPayload_ShouldRoundTripCapabilities()
{
    var payload = new GatewayChannelRegisterPayload
    {
        Channel = "qq",
        Capabilities = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            Attachments = new() { Kinds = [OutboundAttachmentKind.File], MaxBytes = 1024, MaxPerMessage = 5 }
        }
    };

    var json = JsonSerializer.Serialize(payload, GatewayWsFrameSerializer.JsonOptions);
    var back = JsonSerializer.Deserialize<GatewayChannelRegisterPayload>(json, GatewayWsFrameSerializer.JsonOptions)!;
    back.Capabilities.SupportsReplyAnchor.Should().BeTrue();
    back.Capabilities.Attachments!.MaxPerMessage.Should().Be(5);
}

[Fact]
public void ChannelRegisterPayload_WithoutCapabilities_ShouldFailDeserialization()
{
    const string json = """{"channel":"qq"}""";
    Action act = () => JsonSerializer.Deserialize<GatewayChannelRegisterPayload>(json, GatewayWsFrameSerializer.JsonOptions);
    act.Should().Throw<JsonException>();   // 服务端据此回 invalid_capabilities，不注册、不断连
}
```

在 `tests/gateway/Seeing.Gateway.Tests/Core/GatewayChannelRoutingTests.cs` 增加：

```csharp
[Fact]
public void RemoteProxy_ShouldExposeCapabilitiesFromRegister()
{
    var caps = new ChannelCapabilities { SupportsReplyAnchor = true };
    var proxy = new RemoteChannelAdapterProxy("qq", caps, _ => true);
    proxy.Capabilities.Should().BeSameAs(caps);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/abstractions/Seeing.Agent.Abstractions.Tests`
Expected: FAIL（`OutboundAttachmentKind` 等类型不存在）

- [ ] **Step 3: 新建契约类型与扩展方法**

按 Spec §5.1–§5.2 建三个文件（签名严格照 Spec；`Supports` 用 `Kinds.Contains`，`ResolveAttachments` 线性取首个命中，未命中回落 `Attachments`）。

- [ ] **Step 4: 改端口（`ChannelModels.cs` / `ChannelPorts.cs` / `IFileSystem` / `IWorkspacePathGate`）**

- `OutboundMessage.Text` → `required OutboundContent Content`；`OutboundFailureKind` 增 `AttachmentUnsupported / AttachmentTooLarge / AttachmentUnavailable`。
- `IChannelAdapter` 增 `ChannelCapabilities Capabilities { get; }`（无默认实现）。
- `IChannelMessageSink` 两方法改收 `OutboundContent`；新增 `IChannelTargetResolver` + `ChannelTargetResolution`。
- `IChannelDirectory` 增 `ResolveReplyAnchor`。
- `IFileSystem` 增两成员；`IWorkspacePathGate` 增 `EnsureWithinWorkspace`。

- [ ] **Step 5: 改协议与能力链路**

- `GatewayChannelOutboundPayload`：`Text` → `required OutboundContent Content`；`GatewayChannelRegisterPayload.Capabilities` → `required ChannelCapabilities`；删除 `ChannelRegisterCapabilities`。
- `GatewayWebSocketHandler.HandleChannelRegisterAsync`：`try/catch (JsonException)` + `payload.Capabilities is null` → `SendErrorAsync(..., "invalid_capabilities")`，不注册不断连。
- `GatewayConnectionManager.RegisterChannel(string connectionId, string channelId, ChannelCapabilities capabilities)` 保存并传给 `RemoteChannelAdapterProxy`。
- `RemoteChannelAdapterProxy` 构造函数 `(string channelId, ChannelCapabilities capabilities, Func<GatewayChannelOutboundPayload, bool> push)`，`SendAsync` 写 `Content`。
- `WebSocketGatewayClient` 保存能力、注册帧携带、`EnsureConnectedAsync`/重连沿用；`IGatewayConnection`/`IGatewayChannelHostClient` 同签名。
- `IChannelBridge` 增 `Capabilities`、删 `GatewayConnection` 默认实现；两个桥返回适配器能力。

- [ ] **Step 6: 更新所有实现与测试到编译绿灯**

逐项机械改造（不新增行为）：
- `LocalFileSystem`、`WorkspacePathGate`（`EnsureWithinWorkspace` 严格 cwd∪root，抽出与 `EnsureAllowed` 共享的私有判定）。
- `QQChannelAdapter`：注入 `IOptions<QQOptions>`，构造时固化 `_capabilities`（`SupportsReplyAnchor = true`，Kinds 四类，`MaxBytes = _options.MaxMediaBytes`，`MaxPerMessage = 5`，`Guild` 覆盖仅 `Image`）；`SendAsync` 暂取 `message.Content.Text ?? ""`。
- `WeComChannelAdapter`：`Capabilities => new() { SupportsReplyAnchor = false }`；`SendAsync` 取 `Content.Text`。
- 两个桥、`ChannelMessageSink`、`NoopChannelMessageSink`、`ChannelDeliveryScheduleDispatcher`、`GatewayDeliveryNotifier`、`SendMessageTool`（暂以 `new OutboundContent { Text = text }` 调用）、`ChannelDirectoryService`（`RegisterInbound` 内记录 `ReplyToMsgId`，`ResolveReplyAnchor` 返回最后一条）。
- 测试替身与断言同步（`AllowAllGate`/`AllowAllPathGate`/`InMemoryFileSystem`/`GhostEntryFileSystem`/`MemoryChannelAdapter`）。
- `samples/Seeing.Gateway.ChannelHost/Program.cs`：`ConnectAsync(_bridge.ChannelId, _bridge.Capabilities, ct)`。

- [ ] **Step 7: 构建与测试**

Run:
```bash
dotnet build Seeing.Agent.slnx
dotnet build tests/abstractions/Seeing.Agent.Abstractions.Tests
dotnet vstest tests/abstractions/Seeing.Agent.Abstractions.Tests/bin/Debug/net10.0/Seeing.Agent.Abstractions.Tests.dll --TestCaseFilter:"FullyQualifiedName~ChannelCapabilitiesTests"
dotnet build tests/gateway/Seeing.Gateway.Tests
dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll --TestCaseFilter:"FullyQualifiedName~PayloadV2SerializationTests|FullyQualifiedName~GatewayChannelRoutingTests"
```
Expected: 构建 0 error；三个测试类 PASS。

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor(comms): 出站内容与渠道能力契约（无兼容层）"
```

---

## Task 2: Comms 投递管道（能力裁决 + 回复锚点 + 会话目标解析）

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Comms/Delivery/OutboundDeliveryService.cs`
- Modify: `src/capabilities/Seeing.Agent.Comms/Delivery/ChannelMessageSink.cs`
- Create: `src/capabilities/Seeing.Agent.Comms/Delivery/SessionChannelTargetResolver.cs`
- Modify: `src/capabilities/Seeing.Agent.Comms/Extensions/CommsServiceCollectionExtensions.cs`
- Test: `tests/capabilities/Seeing.Agent.Comms.Tests/Delivery/OutboundDeliveryServiceTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Comms.Tests/Delivery/ChannelMessageSinkTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Comms.Tests/Directories/ChannelDirectoryAnchorTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 `ChannelCapabilities` / `OutboundContent` / `IChannelTargetResolver` / `IChannelDirectory.ResolveReplyAnchor`。
- Produces: `SessionChannelTargetResolver : IChannelTargetResolver`；`OutboundDeliveryService.DeliverAsync` 的能力校验与锚点填充语义。

- [ ] **Step 1: 写失败测试**

`OutboundDeliveryServiceTests` 增加（使用现有 `BuildDeliveryService` 辅助；适配器用 `Moq<IChannelAdapter>` 并显式 `SetupGet(a => a.Capabilities)`）：

```csharp
[Fact]
public async Task DeliverAsync_ShouldSucceed_ForTextOnly_WhenChannelSupportsNoAttachments()
{
    var adapter = new Mock<IChannelAdapter>();
    adapter.SetupGet(a => a.ChannelId).Returns("wecom");
    adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
    adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(OutboundDeliveryResult.Ok());
    var delivery = BuildDeliveryService(adapter.Object, directoryWith("wecom", "u1"));

    var result = await delivery.DeliverAsync(TextMessage("wecom", "u1", "hi"));

    result.Delivered.Should().BeTrue();
    adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task DeliverAsync_ShouldRejectAttachment_WhenChannelSupportsNone()
{
    // caps.Attachments == null；消息带 1 个附件
    result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnsupported);
    adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task DeliverAsync_ShouldReject_WhenAttachmentCountExceedsCapability()
{
    // caps: Kinds={File}, MaxBytes=1024, MaxPerMessage=1；消息带 2 个附件
    result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnsupported);
    result.Detail.Should().Contain("1");
    adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task DeliverAsync_ShouldReject_WhenAttachmentSizeExceedsCapability()
{
    // caps.MaxBytes = 1024；attachment.SizeBytes = 2048
    result.FailureKind.Should().Be(OutboundFailureKind.AttachmentTooLarge);
}

[Fact]
public async Task DeliverAsync_ShouldReject_WhenSizeBytesIsNotPositive()
{
    // attachment.SizeBytes = 0
    result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnavailable);
}

[Fact]
public async Task DeliverAsync_ShouldFillReplyAnchor_OnlyForReplyKind()
{
    // 目录已登记 ReplyToMsgId = "m1"；caps.SupportsReplyAnchor = true
    // Reply：captured.Target.ReplyToMsgId == "m1"
    // SystemNotice：captured.Target.ReplyToMsgId == null
}
```

`ChannelDirectoryAnchorTests`：

```csharp
[Fact]
public void RegisterInbound_ShouldRememberLastReplyToMsgId_PerConversation()
{
    var directory = NewDirectory();
    directory.RegisterInbound(Inbound("qq", ConversationKind.Group, "g1", replyTo: "m1"));
    directory.RegisterInbound(Inbound("qq", ConversationKind.Group, "g1", replyTo: "m2"));

    directory.ResolveReplyAnchor(Conversation("qq", ConversationKind.Group, "g1")).Should().Be("m2");
    directory.ResolveReplyAnchor(Conversation("qq", ConversationKind.Group, "g2")).Should().BeNull();
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Comms.Tests`
Expected: FAIL（行为未实现或类型缺失）

- [ ] **Step 3: 实现**

- `ChannelDirectoryService`：`Dictionary<string,string> _replyAnchors`（键 `ChannelKeys.Conversation`），`RegisterInbound` 中 `if (!string.IsNullOrEmpty(message.ReplyToMsgId)) _replyAnchors[key] = message.ReplyToMsgId;`；`ResolveReplyAnchor` 读取。
- `OutboundDeliveryService.DeliverAsync` 按 Spec §7 顺序：适配器命中 → 锚点（仅 `Reply` 且 `SupportsReplyAnchor` 且 `string.IsNullOrEmpty`）→ 附件校验（仅 `Attachments is { Count: > 0 }`）→ 调用适配器。
- `ChannelMessageSink`：`DeliveryKind = Reply`。
- `SessionChannelTargetResolver`：`ISessionManager.LoadAsync(sessionId)`（**不创建会话**；null 或空 id → `NotInDirectory`）+ `ISessionGroupManager.GetParentAsync`（≤8 层）+ Direct 补 `RecipientUserId`。
- DI：`AddSeeingComms` 注册 `AddSingleton<IChannelTargetResolver, SessionChannelTargetResolver>()`（不用 `TryAdd`，保证覆盖工具模块的 Noop）。

- [ ] **Step 4: 构建与测试**

Run:
```bash
dotnet build tests/capabilities/Seeing.Agent.Comms.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Comms.Tests/bin/Debug/net10.0/Seeing.Agent.Comms.Tests.dll --TestCaseFilter:"FullyQualifiedName~OutboundDeliveryServiceTests|FullyQualifiedName~ChannelDirectoryAnchorTests|FullyQualifiedName~ChannelMessageSinkTests"
```
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(comms): 附件能力裁决与被动回复锚点"
```

---

## Task 3: 工具层（Schema、目标解析、路径校验）

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/SendMessageTool.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/ChannelAttachmentNormalizer.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/NoopChannelTargetResolver.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/ChannelDeliveryMessages.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/ChannelMessagingModule.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/Seeing.Agent.Tools.ChannelMessaging.csproj`（无需新增引用，确认即可）
- Test: `tests/capabilities/Seeing.Agent.Tools.ChannelMessaging.Tests/ChannelMessagingToolTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `OutboundContent`/`IChannelTargetResolver`/`IWorkspacePathGate.EnsureWithinWorkspace`/`IFileSystem.GetFileLength|IsDirectory`；Task 2 的 `SessionChannelTargetResolver` 语义。
- Produces: `send_channel_message` 最终 Schema/Description；`ChannelAttachmentNormalizer.Normalize(string sessionId, JsonElement attachment) : NormalizedAttachment`（含 `OutboundAttachment` + `string? Error`）。

- [ ] **Step 1: 写失败测试**

`ChannelMessagingToolTests` 增加（沿用现有 Moq 模式）：

```csharp
[Fact]
public async Task ExecuteAsync_ShouldSendAttachments_WithInferredKindAndSize()
{
    // fs 返回 /ws/report.pdf（长度 100）；调用参数 attachments=[{ path="report.pdf" }]
    // 断言 sink 收到 OutboundContent.Attachments[0].Kind == File、SizeBytes == 100、LocalPath == "/ws/report.pdf"
}

[Fact]
public async Task ExecuteAsync_ShouldReject_WhenPathIsOutsideWorkspace()
{
    // gate.EnsureWithinWorkspace 返回 "越界"
    result.Success.Should().BeFalse();
    result.Error.Should().Contain("越界");
    sink.Verify(s => s.SendToConversationAsync(It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task ExecuteAsync_ShouldReject_WhenChannelIdGivenWithoutTarget()
{
    // 只传 channelId + text
    result.Success.Should().BeFalse();
    result.Title.Should().Be("参数错误");
}

[Fact]
public async Task ExecuteAsync_ShouldReturnResolverFailure_WhenSessionHasNoRoute()
{
    // resolver.Fail(NotInDirectory, "当前会话及父会话均未绑定通道")
    result.Success.Should().BeFalse();
    result.Error.Should().Contain("未绑定通道");
}

[Fact]
public async Task ExecuteAsync_ShouldDefaultToSessionTarget_WhenNoTargetGiven()
{
    // resolver.Ok(target(conversation: group g1))
    // 断言 sink 收到 SendToConversationAsync(target, content)
}

[Fact]
public void ParametersSchema_ShouldAllowEmptyRequired_AndDescribeNestedItems()
{
    // required 为空数组；attachments.items.description 非空；kind.description 含 image/video/audio/file
}
```

越界用例必须把 gate mock 成「严格拒绝」并断言 `AttachmentUnavailable`；同时断言 sink 未被调用（`sink.Verify(..., Times.Never)`）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Tools.ChannelMessaging.Tests`
Expected: FAIL。

- [ ] **Step 3: 实现**

- `SendMessageTool`：按 Spec §8 落 Description 与 Schema；参数自洽校验（互斥/成对/内容非空/只给 `channelId` 报错）；目标解析（显式目标 → 需要 `channelId`：显式或用 resolver 取 `Conversation.ChannelId`，失败原样透传并追加「请提供 channelId」；目标全空 → resolver 完整目标）。
- `ChannelAttachmentNormalizer`：基准目录 `ISessionWorkspaceResolver ?? IWorkspaceProvider`；`IFileSystem.GetFullPath`；`EnsureWithinWorkspace` 拒绝 → `AttachmentUnavailable`；`Exists`/`IsDirectory`/`GetFileLength` 校验；kind 推断（image/video/audio/其余 file）；返回 `SizeBytes`。
- `ChannelDeliveryMessages`：`NotInDirectory` 优先用 `Detail`；成功文案「已提交发送」；新增三类文案（不支持附件 / 附件过大 / 附件不可用）。
- `ChannelMessagingModule`：`DependsOn = ["io.local"]`；`TryAddSingleton<IChannelTargetResolver, NoopChannelTargetResolver>()`（Noop 返回 `ChannelUnavailable("通道投递未配置")`）。
- 注入依赖全部必需（不可空）。

- [ ] **Step 4: 构建与测试**

Run:
```bash
dotnet build tests/capabilities/Seeing.Agent.Tools.ChannelMessaging.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Tools.ChannelMessaging.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.ChannelMessaging.Tests.dll --TestCaseFilter:"FullyQualifiedName~ChannelMessagingToolTests"
dotnet build tests/capabilities/Seeing.Agent.Tools.Metadata.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolMetadataTests"
```
Expected: PASS；Schema 机械校验（R1–R9）通过。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(channel-messaging): send_channel_message 支持工作区附件"
```

---

## Task 4: QQ 适配器（结构化媒体 + 逐条被动额度 + 删除旧文本协议）

**Files:**
- Delete: `src/gateway/Seeing.Gateway.QQ/QQOutboundComposer.cs`
- Delete: `tests/gateway/Seeing.Gateway.QQ.Tests/QQOutboundComposerTests.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/Connection/QQHttpApiClient.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelAdapter.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQTextSanitizer.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQOptions.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelBridge.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelPlugin.cs`（删除 `IChannelAdapter` 死注册）
- Test: `tests/gateway/Seeing.Gateway.QQ.Tests/QQChannelAdapterTests.cs`
- Test: `tests/gateway/Seeing.Gateway.QQ.Tests/QQTextSanitizerTests.cs`
- Test: `tests/gateway/Seeing.Gateway.QQ.Tests/QQHttpApiClientBodyTests.cs`（如涉及请求体）

**Interfaces:**
- Consumes: Task 1 的 `OutboundContent`/`OutboundAttachment`/`ChannelCapabilities`。
- Produces: `QQHttpApiClient.SendAttachmentAsync(ParsedQQMessage target, OutboundAttachment attachment, CancellationToken ct = default) : Task<OutboundDeliveryResult>`；`QQChannelAdapter.SendContentAsync(ParsedQQMessage, OutboundContent, CancellationToken)`（private）。

- [ ] **Step 1: 写失败测试**

`QQChannelAdapterTests` 增加/改写：

```csharp
[Fact]
public async Task SendAsync_WithTwoAttachments_ShouldAcquirePassivePerPlatformMessage()
{
    // tracker(replyLimit: 1)；消息：1 段文本 + 1 个附件
    // 断言：文本请求带 msg_id；附件请求不带 msg_id（额度已耗尽）
    textBodies.Should().ContainSingle().Which.Should().Contain("\"msg_id\"");
    mediaBodies.Should().ContainSingle().Which.Should().NotContain("\"msg_id\"");
}

[Fact]
public async Task SendAsync_ShouldReportPartialFailure_WhenSecondAttachmentFails()
{
    // 第 1 个附件成功、第 2 个上传失败
    result.Delivered.Should().BeFalse();
    result.FailureKind.Should().Be(OutboundFailureKind.PlatformError);
    result.Detail.Should().Contain("1").And.Contain("2");
}

[Fact]
public async Task SendAsync_ShouldMapOversizedFile_ToAttachmentTooLarge_BeforeReading()
{
    // FileInfo.Length > caps.MaxBytes，且不触发任何上传请求
    result.FailureKind.Should().Be(OutboundFailureKind.AttachmentTooLarge);
    uploadRequestCount.Should().Be(0);
}

[Fact]
public async Task SendAsync_ShouldPropagateCancellation_WithoutSendingRemainingItems()
{
    // 第 1 条发出后取消
    await FluentActions.Awaiting(() => adapter.SendAsync(message, cts.Token))
        .Should().ThrowAsync<OperationCanceledException>();
    mediaBodies.Should().HaveCount(0);
}

[Fact]
public async Task SendAsync_ShouldRejectNonImage_ForGuild()
{
    // Kind=File 发到 guild
    result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnsupported);
}
```

删除 `QQOutboundComposerTests`；`QQTextSanitizerTests` 删除 `ExtractImages_*` 用例。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/gateway/Seeing.Gateway.QQ.Tests`
Expected: FAIL。

- [ ] **Step 3: 实现**

- 删除 `QQOutboundComposer` 整文件与 `QQTextSanitizer.ExtractImages`。
- `QQHttpApiClient`：删 `SendReplyAsync`/`SendImagesAsync`/`SendMediaItemsAsync`/`SendGuildImageAsync`/`UploadMediaAsync`；`SendGuildImageFileAsync` 去掉 `msgId` 形参、改用 `target.MsgId`；新增 `SendAttachmentAsync`（`FileInfo.Length` 预检 → 上传 → `SendMediaMessageAsync`，`msg_id` 取 `target.MsgId`，返回 `OutboundDeliveryResult`）；内部 media-type helper（`OutboundAttachmentKind → file_type`，`.silk/.amr → 3`）；429 映射抽到 `Connection` internal helper。
- `QQChannelAdapter`：`SendContentAsync`（逐块/逐附件 `ApplyPassiveWindow`；文本 catch 顺序：用户取消 throw → 其他映射失败结果；聚合部分失败）；`SendAsync = ToParsed + SendContentAsync`；删除 `Translate`；`SendParsedAsync` 走文本分支、空文本 `Ok()`；`Capabilities` 构造固化。
- `QQOptions`：`PassiveReplyLimitPerMsg` 默认 5，同步 XML/`[Display]` 文案。
- `QQChannelBridge`：忽略条件改「文本与附件都为空」。
- `QQChannelPlugin`：删除 `services.AddSingleton<IChannelAdapter>(...)`（无消费方；桥依赖具体 `QQChannelAdapter`）。

- [ ] **Step 4: 构建与测试**

Run:
```bash
dotnet build tests/gateway/Seeing.Gateway.QQ.Tests
dotnet vstest tests/gateway/Seeing.Gateway.QQ.Tests/bin/Debug/net10.0/Seeing.Gateway.QQ.Tests.dll --TestCaseFilter:"FullyQualifiedName~QQChannelAdapterTests|FullyQualifiedName~QQTextSanitizerTests|FullyQualifiedName~QQPassiveWindowTrackerTests"
```
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(qq): 结构化附件出站，删除文本媒体标记协议"
```

---

## Task 5: WeCom 适配器与宿主接线

**Files:**
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComChannelAdapter.cs`
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComChannelPlugin.cs`（删除 `IChannelAdapter` 死注册）
- Modify: `src/gateway/Seeing.Gateway.WeCom/Extensions/WeComServiceCollectionExtensions.cs`（如构造函数有变）
- Modify: `samples/Seeing.Gateway.Server/Program.cs`
- Modify: `samples/Seeing.Gateway.Server/Seeing.Gateway.Server.csproj`
- Test: `tests/gateway/Seeing.Gateway.WeCom.Tests/WeComChannelAdapterTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ChannelCapabilities`/`OutboundContent`；Task 3 的 `ChannelMessagingModule`。
- Produces: WeCom 显式空能力与 `Content.Text` 语义；Gateway.Server 挂载通联工具模块。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Capabilities_ShouldDeclareNoAttachmentsAndNoReplyAnchor()
{
    adapter.Capabilities.Attachments.Should().BeNull();
    adapter.Capabilities.SupportsReplyAnchor.Should().BeFalse();
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/gateway/Seeing.Gateway.WeCom.Tests`

- [ ] **Step 3: 实现**

- `WeComChannelAdapter.Capabilities => new() { SupportsReplyAnchor = false }`；`SendAsync` 从 `message.Content.Text` 取文本。
- `WeComChannelPlugin`：删除 `services.AddSingleton<IChannelAdapter>(...)`（无消费方）。
- `samples/Seeing.Gateway.Server/Program.cs`：`builder.Services.AddSeeingModule<ChannelMessagingModule>(registry);`。
- `Seeing.Gateway.Server.csproj`：加 `ProjectReference` 指向 `..\..\src\capabilities\Seeing.Agent.Tools.ChannelMessaging\Seeing.Agent.Tools.ChannelMessaging.csproj`。

- [ ] **Step 4: 构建与测试**

Run:
```bash
dotnet build tests/gateway/Seeing.Gateway.WeCom.Tests
dotnet vstest tests/gateway/Seeing.Gateway.WeCom.Tests/bin/Debug/net10.0/Seeing.Gateway.WeCom.Tests.dll
dotnet build samples/Seeing.Gateway.Server
```
Expected: PASS + 样例构建成功。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(wecom,server): 显式空能力并挂载通联工具模块"
```

---

## Task 6: 文档与发布说明

**Files:**
- Modify: `docs/architecture/17-channel-comms-release-notes.md`
- Modify: `AGENTS.md`（WHERE TO LOOK 增一行）
- Modify: `docs/architecture/06-compliance-audit.md`（纯函数豁免清单登记 `ChannelCapabilitiesExtensions`）

**Interfaces:** 无代码接口。

- [ ] **Step 1: 更新 17 号 Release Notes**

新增「2026-10-23 出站附件」小节：破坏性变更清单（`OutboundMessage.Content`、`IChannelMessageSink`、`IChannelAdapter.Capabilities`、注册帧能力、`IGatewayConnection.ConnectAsync`、QQ 文本媒体标记删除）、能力模型说明、已知边界（WeCom 不支持附件/锚点、进程外无回执、同机文件）。

- [ ] **Step 2: 更新 AGENTS.md 与 06 审计**

- WHERE TO LOOK 增：`| 出站附件 / 渠道能力 | src/abstractions/.../Channels/ChannelAttachments.cs + ChannelCapabilities.cs；Comms 投递；QQ SendAttachmentAsync | 见 docs 17 |`
- 06 的纯函数豁免清单增 `ChannelCapabilitiesExtensions` 两方法。

- [ ] **Step 3: 校验文档**

Run: `git diff --check`
Expected: 无空白错误。

- [ ] **Step 4: Commit**

```bash
git add docs AGENTS.md
git commit -m "docs: 出站附件能力发布说明与索引"
```

---

## Task 7: 全量验证与残留清理

**Files:** 无新增；只修复验证发现的问题。

- [ ] **Step 1: 全量构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 error。

- [ ] **Step 2: 跑受影响测试项目**

```bash
dotnet vstest tests/abstractions/Seeing.Agent.Abstractions.Tests/bin/Debug/net10.0/Seeing.Agent.Abstractions.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspacePathGateSessionRootTests"
dotnet vstest tests/capabilities/Seeing.Agent.Comms.Tests/bin/Debug/net10.0/Seeing.Agent.Comms.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Tools.ChannelMessaging.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.ChannelMessaging.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll
dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll
dotnet vstest tests/gateway/Seeing.Gateway.Client.Tests/bin/Debug/net10.0/Seeing.Gateway.Client.Tests.dll
dotnet vstest tests/gateway/Seeing.Gateway.QQ.Tests/bin/Debug/net10.0/Seeing.Gateway.QQ.Tests.dll
dotnet vstest tests/gateway/Seeing.Gateway.WeCom.Tests/bin/Debug/net10.0/Seeing.Gateway.WeCom.Tests.dll
dotnet vstest tests/gateway/Seeing.Agent.Comms.IntegrationTests/bin/Debug/net10.0/Seeing.Agent.Comms.IntegrationTests.dll
```
Expected: 全 PASS（逐项目构建后执行）。

- [ ] **Step 3: 残留扫描**

```bash
rg -n "QQOutboundComposer|QQOutboundMediaItem|QQOutboundMediaKind|ExtractImages|ChannelRegisterCapabilities|SendAttachmentsAsync|QQMediaSendResult|SendToSessionAsync" src tests samples
rg -n "message\.Text|payload\.Text" src
```
Expected: 第一组 0 命中；第二组仅剩文档/注释中的合法引用（无生产读点）。

- [ ] **Step 4: 提交修复（如有）**

```bash
git add -A
git commit -m "test: 全量验证与残留清理"
```

---

## 关键风险与回退

- **被动额度数值**：默认 5 是实施前的决策值；Task 4 开始前按 QQ 官方文档核对，如上限小于 5 则改回实际值并同步 17 号文档（行为不变：逐条计数 + 耗尽降级）。
- **并行冲突**：Wave 2 任务文件互斥；若合并出现冲突，按任务号顺序重放该任务，不要手工混改。
- **Gate 1 不通过**：只回改 Task 1，不进入 Wave 2。
- **Gate 2 不通过**：按指出的任务回改（仅该任务范围内），重新合并后复审一次。
