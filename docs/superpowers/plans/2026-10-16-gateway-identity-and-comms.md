# Gateway 定位重构与通联子系统 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan — 但按本计划「执行模型」修订：**任务按波次并行派发（每波内任务互不触碰相同文件），审查只在 4 个审查门进行，不逐任务审查**。

**Goal:** 按 spec `docs/superpowers/specs/2026-10-16-gateway-identity-and-outbound-delivery-design.md`（v3.1）落地：契约层 `Abstractions/Channels/`、通联子系统 `Seeing.Agent.Comms`、协议 v2、ISessionClient/HostClient 双契约、Gateway Server 收窄、QQ/WeCom 适配器化、工具能力包、架构债收敛。

**Architecture:** 四波并行 + 四审查门。Wave 1 契约与映射器（正交）→ Wave 2 Comms/运行时集成/客户端契约（三正交）→ Wave 3 Server 收窄/QQ/WeCom/工具+WebUI（四正交）→ Wave 4 集成联调与文档。

**Tech Stack:** .NET 10 / C# / xUnit 2.9 + FluentAssertions 6.12 + Moq 4.20 / CPM（`Directory.Packages.props`）/ `.slnx`

## Global Constraints（所有任务隐含遵守）

- 目标框架 net10.0；包版本经 CPM，禁止手写版本号
- 能力包（`Seeing.Agent.Comms`、`Seeing.Agent.Tools.ChannelMessaging`、QQ/WeCom 适配器包）**只依赖** `Seeing.Agent.Abstractions` + 原语（`Seeing.Session`/`Seeing.TokenEstimation`/`Seeing.ConfigSchema`）+ `Seeing.Gateway`（QQ/WeCom 适配器为 gateway 家族插件包，例外允许）
- **禁止**引用 `Seeing.Agent.Core`/`Seeing.Agent.Hosting` 具体类型；Hook 用 `HookPoints.*` 常量
- SeeingAgent/Comms 配置只写 `.seeing/seeing.json`；数据文件放 `~/.seeing/comms/`
- 测试命令一律 `dotnet build <test-project>` 后 `dotnet vstest <dll>`（MTP 在此环境发现不了测试）
- 每任务内小步提交；提交信息格式 `feat|refactor|test|docs: <中文描述>`
- 命名：接口前缀 `I`，结果类后缀 `Result`，异步 `Async` 后缀；接口名 = 实现类名去 `I`
- 文件路径分隔符统一 `/`（跨平台）

---

## 执行模型（并行派发 + 审查门协议）

```
Wave 1:  [T1 契约层]                     ∥ [T2 映射器合并]
                    ──── 审查门 ①（契约 API + 映射器回归）────
Wave 2:  [T3 Comms 核心] ∥ [T4 运行时集成+协议v2] ∥ [T5 客户端双契约+泵]
                    ──── 审查门 ②（全量 build+单测 + 接口契合审查）────
Wave 3:  [T6 Server收窄+调度迁移] ∥ [T7 QQ适配器] ∥ [T8 WeCom适配器] ∥ [T9 工具包+WebUI UI]
                    ──── 审查门 ③（集成回归：调度双场景+桥 RunCompleted+保留端点）────
Wave 4:  [T10 端到端双拓扑 + 文档]
                    ──── 审查门 ④（spec 覆盖核对 + e2e 绿）────
```

**派发规则：**
- 每任务派发一个全新 generalPurpose 子代理，prompt = 本计划中该任务全文 + Global Constraints + spec 路径（令其自读 spec 对应节）
- 同波任务**文件集互斥**（见各任务 Files 块）；子代理严禁越界改文件
- 波内任务失败不阻塞同波其他任务；失败任务修复后单独补验，重过当前审查门才进下一波

**审查门协议（仅 4 处，不逐任务审查）：**
- 门①②③：①`dotnet build Seeing.Agent.slnx` 全绿 ②全量 `dotnet vstest`（tests/ 四个相关测试项目）绿 ③派 1 个审查子代理按「门的检查清单」抽查接口契合（只查跨任务缝隙，不做全码审）
- 门④：spec 覆盖逐节核对 + e2e 测试绿 + 文档提交验证
- 门未过：定位到具体任务 → 修复 → 只重跑该门，不回退已完成波次

---

# Wave 1

### Task 1: 契约层 `Abstractions/Channels/`

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelModels.cs`（全部 DTO）
- Create: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelPorts.cs`（全部端口）
- Create: `src/abstractions/Seeing.Agent.Abstractions/Channels/ChannelInbound.cs`（入站消息）
- Create: `tests/abstractions/Seeing.Agent.Abstractions.Tests/Channels/ChannelModelsTests.cs`
- Modify: `Seeing.Agent.slnx`（若目录内新项目才需；本任务仅加文件，不动 slnx）

纯新增文件，**零修改现有文件**（运行时集成在 T4）。

**Interfaces:**
- Consumes: 无（全新）
- Produces（后续所有任务的精确契约，签名一字不差）:

```csharp
namespace Seeing.Agent.Abstractions.Channels;

// ── ChannelModels.cs ──
public enum ConversationKind { Direct, Group, Guild }

public sealed record ChannelUserIdentity
{
    public required string ChannelId { get; init; }        // "qq" / "wecom"
    public required string ExternalId { get; init; }       // union openid / wecom userid
    public string? DisplayName { get; init; }
}

public sealed record ConversationInfo
{
    public required string ChannelId { get; init; }
    public required ConversationKind Kind { get; init; }
    public required string ExternalId { get; init; }       // Direct=用户 ExternalId；Group=group_openid；Guild=channel_id
    public string? Title { get; init; }
}

public enum OutboundDeliveryKind { Reply, Push, SystemNotice }

public sealed record DeliveryTarget
{
    public required ConversationInfo Conversation { get; init; }
    public string? RecipientUserId { get; init; }          // 群内定向/@（ChannelUserIdentity.ExternalId）
    public string? ReplyToMsgId { get; init; }             // 被动回复窗口内携带
}

public sealed record OutboundMessage
{
    public required DeliveryTarget Target { get; init; }
    public required OutboundDeliveryKind DeliveryKind { get; init; }
    public required string Text { get; init; }
    public string? Source { get; init; }                   // "scheduler.cron" / "tool.send_message"
    public string? CorrelationSessionId { get; init; }
    public string? JobId { get; init; }
}

public enum OutboundFailureKind { NotInDirectory, OptedOut, RateLimited, ChannelUnavailable, PlatformError }

public sealed record OutboundDeliveryResult
{
    public required bool Delivered { get; init; }
    public OutboundFailureKind? FailureKind { get; init; }
    public string? Detail { get; init; }
    public static OutboundDeliveryResult Ok() => new() { Delivered = true };
    public static OutboundDeliveryResult Fail(OutboundFailureKind kind, string? detail = null)
        => new() { Delivered = false, FailureKind = kind, Detail = detail };
}

public sealed record ChannelUserEntry
{
    public required string ChannelId { get; init; }
    public required string ExternalId { get; init; }
    public string? DisplayName { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required bool OptedOut { get; init; }
}

public sealed record ConversationEntry
{
    public required ConversationInfo Conversation { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required IReadOnlyList<string> MemberExternalIds { get; init; }  // Group 场景惰性发现
}

// ── ChannelPorts.cs ──
public interface IChannelDirectoryStore          // Store：纯存取
{
    Task SaveUserAsync(ChannelUserEntry user, CancellationToken ct = default);
    Task SaveConversationAsync(ConversationEntry entry, CancellationToken ct = default);
    Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default);
    Task<ConversationEntry?> FindConversationAsync(string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);
    Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(string channelId, int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(string channelId, int skip, int take, CancellationToken ct = default);
    Task LoadIntoAsync(Action<ChannelUserEntry, ConversationEntry?> sink, CancellationToken ct = default);  // 启动载入
}

public interface IChannelDirectory              // Manager：登记/查询/退订/频控
{
    void RegisterInbound(ChannelInboundMessage message);   // 提交方显式调用；入站登记不清除 OptedOut
    Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(string channelId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(string channelId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default);
    Task SetOptOutAsync(string channelId, string externalId, bool optedOut, CancellationToken ct = default);
    bool IsRateLimited(string channelId, string externalId);   // 滑窗判定（已投递计数）
    void RecordDelivery(string channelId, string externalId);  // 投递成功后计数
}

public interface IChannelMessageSink            // Sink：agent 工具唯一单向出口
{
    Task<OutboundDeliveryResult> SendToUserAsync(string channelId, string userExternalId, string text, CancellationToken ct = default);
    Task<OutboundDeliveryResult> SendToConversationAsync(DeliveryTarget target, string text, CancellationToken ct = default);
}

public interface IChannelAdapter                // Provider：通道适配器
{
    string ChannelId { get; }
    Task<OutboundDeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct = default);
}

public interface IChannelAdapterRegistry        // Registry：单实例 per channel
{
    void Register(IChannelAdapter adapter);     // 同 ChannelId 重复注册：后注册者生效 + 记 Warning
    bool TryGet(string channelId, out IChannelAdapter? adapter);
    IReadOnlyList<string> RegisteredChannels { get; }
    void Unregister(string channelId);
}

public interface IChannelDeliveryNotifier       // Sink：投递通知出口（默认 no-op；Server 实现）
{
    Task NotifyDeliveredAsync(OutboundMessage message, OutboundDeliveryResult result, CancellationToken ct = default);
}

// ── ChannelInbound.cs ──
public sealed record ChannelInboundMessage
{
    public required ChannelUserIdentity Sender { get; init; }
    public required ConversationInfo Conversation { get; init; }
    public string? ReplyToMsgId { get; init; }
    public required string SessionId { get; init; }        // 适配器命名的会话 ID（仅命名，不承载路由）
    public required string Text { get; init; }
    public string? CommandToken { get; init; }             // "new"|"clear"|"subscribe"|"unsubscribe"|null
    public IReadOnlyList<object>? AttachmentParts { get; init; }  // 媒体部件由适配器转 GatewayContentPart（T4 定型）
}
```

- [ ] **Step 1**：创建三个契约文件（上述完整代码；`AttachmentParts` 的占位 object 在 T4 定型为 `Seeing.Gateway.Models.GatewayContentPart`——本任务不引 Seeing.Gateway，保持 `object`，T4 改签名并同步注释）
- [ ] **Step 2**：写测试 `ChannelModelsTests.cs`：record 等值语义、`OutboundDeliveryResult.Ok/Fail` 工厂、`ConversationKind` 序列化 camelCase（用 `GatewayWsFrameSerializer.JsonOptions` 同构的 `JsonSerializerOptions` 验证 `"direct"/"group"/"guild"`）
- [ ] **Step 3**：`dotnet build src/abstractions/Seeing.Agent.Abstractions` → 绿；`dotnet vstest`（Abstractions 测试项目 dll）→ 绿
- [ ] **Step 4**：Commit `feat: 契约层 Abstractions/Channels（DTO+端口+入站消息）`

---

### Task 2: 事件映射器合并（协议层基准）

**Files:**
- Modify: `src/gateway/Seeing.Gateway/Mapping/GatewayEventMapper.cs`（补 4 类型；`Map` 保持 throw、外层 `TryMap` 包一层跳过语义）
- Create: `src/gateway/Seeing.Gateway/Mapping/GatewayEventMapperExtensions.cs`（`TryMap` 扩展）
- Delete: `src/gateway/Seeing.Agent.Gateway/Core/GatewayEventMapper.cs`（内含 `ChatEventMapper`）
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs`（`ChatEventMapper.TryMap` → `GatewayEventMapper.TryMap`，using 改 `Seeing.Gateway.Mapping`）
- Test: `tests/gateway/Seeing.Gateway.Tests/Mapping/GatewayEventMapperMergeTests.cs`（不存在则创建该测试项目目录结构，按既有测试项目 csproj 模式）

**Interfaces:**
- Consumes: `IMessageEvent` 层级（`Abstractions/Events/MessageEventTypes.cs`）、`GatewayEvent/GatewayEventData`（`Seeing.Gateway/Models`）
- Produces: `static bool GatewayEventMapper.TryMap(IMessageEvent evt, out GatewayEvent? gatewayEvent)`（跳过未知，返回 false）；`Map` 保持现有公开签名。**`SourceType` 一律 `= evt.Type`**（修复 `loop_complete` ≠ `loop.complete` bug）

- [ ] **Step 1**：写失败测试（先建测试）：

```csharp
[Fact]
public void TryMap_LoopCancelled_MapsToCancelled()
{
    var evt = new LoopCancelledEvent { SessionId = "s", LoopId = "l", Reason = "user" };
    GatewayEventMapper.TryMap(evt, out var ge).Should().BeTrue();
    ge!.SourceType.Should().Be(MessageEventType.LoopCancelled); // "loop.cancelled"——与 evt.Type 严格一致
}

[Fact]
public void TryMap_CommandResult_Navigate_SkillContent_AreMapped()
{
    GatewayEventMapper.TryMap(new CommandResultEvent { SessionId = "s", Success = true, Message = "m" }, out var a).Should().BeTrue();
    a!.Data!.Text.Should().Be("m");
    GatewayEventMapper.TryMap(new NavigateEvent { SessionId = "s", Target = "/x" }, out var b).Should().BeTrue();
    GatewayEventMapper.TryMap(new SkillContentEvent { SessionId = "s", ExpandedContent = "c" }, out var c).Should().BeTrue();
}

[Fact]
public void TryMap_UnknownEvent_ReturnsFalse()
{
    GatewayEventMapper.TryMap(new SessionUpdatedEvent { SessionId = "s", Session = null! }, out _).Should().BeFalse();
}

[Fact]
public void TryMap_LoopComplete_SourceTypeUsesEventTypeConstant()  // 回归现行 bug
{
    var evt = new LoopCompleteEvent { SessionId = "s", LoopId = "l", Success = true };
    GatewayEventMapper.TryMap(evt, out var ge).Should().BeTrue();
    ge!.SourceType.Should().Be(MessageEventType.LoopComplete);   // "loop.complete"，绝不允许硬编码 "loop_complete"
}
```

（事件构造按实际 record 定义调整必填字段；跑前先读 `MessageEventTypes.cs` 对齐字段名。）

- [ ] **Step 2**：`dotnet build tests/gateway/Seeing.Gateway.Tests` → 编译失败（TryMap 不存在/类型缺失）——预期红
- [ ] **Step 3**：实现——协议层 `GatewayEventMapper.cs` switch 内补四 case（字段映射抄原 `ChatEventMapper` 对应 case：`CommandResult`→`GatewayEventObject.Message`+`Data.Text/Success`；`Navigate`→`Message`+`Data.Text=Target`；`SkillContent`→`Content`+`Data.Text=ExpandedContent`；`LoopCancelled`→抄协议层既有 `MapLoopCancelled`）；新增 `GatewayEventMapperExtensions.TryMap`（try{Map}catch(NotSupportedException){false}；`SessionUpdatedEvent` 显式 false）。删除 `Agent.Gateway/Core/GatewayEventMapper.cs`；`GatewayOrchestratorV2.cs:236` 的 `ChatEventMapper.TryMap` 改 `GatewayEventMapper.TryMap`（加 using `Seeing.Gateway.Mapping`）
- [ ] **Step 4**：build + vstest 绿（含既有 `GatewayEventMapper` 测试不回归——注意协议层 `Map` 对 `StreamComplete` 的 tool/assistant 分流、`FilterThinking` 默认值维持现状不动）
- [ ] **Step 5**：Commit `refactor: 事件映射器合并至协议层单一 GatewayEventMapper，修复 loop.complete SourceType bug`

---

## ─── 审查门 ① ───

**检查清单：** ①全解决方案 build 绿 ②`ChannelModelsTests`+`GatewayEventMapperMergeTests` 绿 ③审查子代理抽查：T1 契约签名与本计划一字不差（后续 9 个任务的编译前提）；T2 的 `GatewayAssistantReplyCollector.LoopCompleteSourceType` 常量与映射产出 `SourceType` 恒等（`"loop.complete"` 命中验证）

---

# Wave 2

### Task 3: 通联子系统包 `Seeing.Agent.Comms` 核心

**Files:**
- Create: `src/capabilities/Seeing.Agent.Comms/Seeing.Agent.Comms.csproj`（引用：`Seeing.Agent.Abstractions`、`Seeing.Session`；CPM）
- Create: `src/capabilities/Seeing.Agent.Comms/CommsOptions.cs`
- Create: `src/capabilities/Seeing.Agent.Comms/CommsModule.cs`（`ISeeingModule`）
- Create: `src/capabilities/Seeing.Agent.Comms/Directories/JsonChannelDirectoryStore.cs`（`~/.seeing/comms/directory.json` 原子写）
- Create: `src/capabilities/Seeing.Agent.Comms/Directories/ChannelDirectoryService.cs`
- Create: `src/capabilities/Seeing.Agent.Comms/Delivery/OutboundDeliveryService.cs`
- Create: `src/capabilities/Seeing.Agent.Comms/Delivery/ChannelAdapterRegistry.cs`
- Create: `src/capabilities/Seeing.Agent.Comms/Delivery/ChannelMessageSink.cs`
- Create: `src/capabilities/Seeing.Agent.Comms/Delivery/NoopDeliveryNotifier.cs`
- Create: `src/capabilities/Seeing.Agent.Comms/Extensions/CommsServiceCollectionExtensions.cs`
- Modify: `Seeing.Agent.slnx`（项目加目录映射；按 slnx 既有 src/capabilities 条目样式）
- Test: `tests/capabilities/Seeing.Agent.Comms.Tests/`（新建测试项目）

**Interfaces:**
- Consumes: T1 全部契约；`IConfigSectionRegistry`/`ISeeingModule`/`ConfigSectionMeta`（Abstractions/Modules）
- Produces:
  - `AddSeeingComms(this IServiceCollection, IConfigSectionRegistry registry)`（注册节 `Comms`/`seeing.json`/`ConfigScope.Both`；singleton：Store/Directory/Registry/DeliveryService/Sink/Notifier）
  - `OutboundDeliveryService : ` 公开 `Task<OutboundDeliveryResult> DeliverAsync(OutboundMessage message, CancellationToken ct = default)`——五步管道：目标解析（`Target.Conversation` 直用；userId 解析 Direct 对话由 Sink 做）→ 准入（`FindUserAsync` 不存在→`NotInDirectory`；`OptedOut`→`OptedOut`；`IsRateLimited`→`RateLimited`）→ `IChannelAdapterRegistry.TryGet`（无→`ChannelUnavailable`）→ `adapter.SendAsync`（失败透传 `PlatformError`）→ 成功后 `RecordDelivery` + `NotifyDeliveredAsync`
  - `CommsOptions { RateLimitPerHour = 20, DirectoryFilePath = null /*默认 ~/.seeing/comms/directory.json*/, AuditLogPath = null }`
  - `ChannelMessageSink : IChannelMessageSink`——`SendToUserAsync`：查用户→构造 `DeliveryTarget{ Conversation = new(){ ChannelId, Kind=Direct, ExternalId=userExternalId } }`→`DeliverAsync`；`SendToConversationAsync` 直通

- [ ] **Step 1**：csproj + slnx + `CommsOptions` + `CommsModule`（Activate 挂载为空实现骨架——工具包 T9 才需要 Activate 挂载点，本任务模块只做 DI 注册）+ DI 扩展。Build 绿后 commit `feat: Seeing.Agent.Comms 包骨架与 DI`
- [ ] **Step 2**：失败测试（目录/投递核心行为，xUnit + FluentAssertions + Moq）：

```csharp
[Fact]
public async Task DeliverAsync_UserNotInDirectory_ReturnsNotInDirectory()
{
    var directory = new Mock<IChannelDirectory>();   // Directory 是 Manager 端口，DeliveryService 依赖接口
    directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>())).ReturnsAsync((ChannelUserEntry?)null);
    var svc = BuildDeliveryService(directory.Object, registry: new ChannelAdapterRegistry());
    var result = await svc.DeliverAsync(BuildPush("qq", "u1"));
    result.FailureKind.Should().Be(OutboundFailureKind.NotInDirectory);
}

[Fact]
public async Task DeliverAsync_OptedOut_ReturnsOptedOut_AndInboundRegisterDoesNotClearIt()
// 断言两件事：投递拒绝 OptedOut；RegisterInbound 之后 FindUserAsync 返回的条目 OptedOut 仍为 true

[Fact]
public async Task DeliverAsync_RateLimited_AfterLimitReached()
// RateLimitPerHour=2：两次成功投递后第三次 → RateLimited；滑窗内 RecordDelivery 计数生效

[Fact]
public async Task DeliverAsync_NoAdapterRegistered_ReturnsChannelUnavailable()

[Fact]
public async Task DeliverAsync_Success_CallsAdapter_AndNotifier_AndRecordsDelivery()
// Mock IChannelAdapter.SendAsync 返回 Ok；断言 adapter 收到的 OutboundMessage 字段完整透传、Notifier.NotifyDeliveredAsync 被调、RecordDelivery 被调

[Fact]
public void ChannelAdapterRegistry_DuplicateRegistration_LastWins_AndKeepsChannelsList()
```

（`BuildPush` 辅助构造 `OutboundMessage`；`ChannelDirectoryService` 的频控/退训测试用真实类 + Mock Store。）

- [ ] **Step 3**：实现全部类（`JsonChannelDirectoryStore`：启动 `LoadIntoAsync` 载入内存 + 每次写后原子落盘（temp+move）；`ChannelDirectoryService`：内存缓存 + `RegisterInbound` 登记 user/conversation/member + lastSeen 刷新、**不覆盖 OptedOut**；滑窗 `Queue<DateTimeOffset>` 每用户）
- [ ] **Step 4**：build + vstest 绿；commit `feat: Comms 目录/投递管道/注册表/Sink 实现`
- [ ] **Step 5**：`dotnet pack src/capabilities/Seeing.Agent.Comms -c Debug` 验证包可产出（不发布）；commit

---

### Task 4: 运行时集成 + 协议 v2

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxMessage.cs`（删 `ChannelId`/`UserId`；增 `Conversation`/`ReplyToMsgId`/`SenderIdentity`）
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Runtime/InboxMetadataKeys.cs`（删 `ChannelId`/`UserId` 键；增 `Conversation = "inbox.conversation"`——值为 `ConversationInfo` 的 camelCase JSON）
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs:435-438`（metadata 写入改结构化）
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs:607-618`（`TryBackfillSessionOutbound` → `TryBackfillChannelRoute`：空才写 `session.ChannelRoute`）
- Modify: `src/primitives/Seeing.Session/Core/SessionData.cs`（删 `ChannelId`/`UserId`（`:76-79`）；增 `public ConversationInfo? ChannelRoute`——**注意**：`Seeing.Session` 不得引用 `Seeing.Agent.Abstractions`！`ConversationInfo` 放不下 → 在 `Seeing.Session` 定义同名镜像 record `SessionChannelRoute { ChannelId, Kind: string, ExternalId, Title }`，Abstractions 侧提供 `ConversationInfo.ToSessionRoute()` 映射扩展（放 `Channels/`，依赖方向 Abstractions→Session 合法）
- Modify: `src/primitives/Seeing.Session/Core/SessionSummary.cs:25-26`（Channel 列投影改 `ChannelRoute?.ChannelId`）
- Modify: `src/primitives/Seeing.Session/Persistence/FileSessionStore.cs:666-667`（读取默认值同步）
- Modify: `src/gateway/Seeing.Gateway/Models/GatewayRequest.cs`（增 `Sender`/`Conversation`/`ReplyToMsgId`，类型为 T1 DTO——Seeing.Gateway 已依赖 Abstractions；删 `UserId`）
- Modify: `src/gateway/Seeing.Gateway/Protocol/GatewayWsFrame.cs`（`GatewayChannelOutboundPayload` v2：增 `conversation`/`recipientUserId`/`deliveryKind`/`replyToMsgId`，`SessionId` 改名 `CorrelationSessionId`；`GatewayChannelRegisterPayload` 增 `capabilities`；`GatewayCancelPayload` 增 `Mode`）
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs:287-295`（`LastNonEmptyMeta` 聚合 `Conversation`——结构化键沿用非空判定）
- Test: 更新 `tests/hosting/.../ExecutionRunnerOutboundBackfillTests.cs`、`tests/primitives/Seeing.Session.Tests/.../SessionDataTests.cs:93-97`；新增 `tests/gateway/Seeing.Gateway.Tests/Protocol/PayloadV2SerializationTests.cs`

**Interfaces:**
- Consumes: T1 `ChannelUserIdentity/ConversationInfo/ConversationKind`
- Produces: `InboxMessage.Conversation: ConversationInfo?`、`SessionData.ChannelRoute: SessionChannelRoute?`、`ConversationInfo.ToSessionRoute(): SessionChannelRoute`、`GatewayRequest.Sender/Conversation/ReplyToMsgId`、payload v2 序列化形状（camelCase；`GatewayCancelPayload.Mode: string?`，null = `"interrupt"`）

- [ ] **Step 1**：失败测试：`PayloadV2SerializationTests`——`GatewayChannelOutboundPayload` 序列化产出 `{ "channel":..., "conversation":{"kind":"group","externalId":...}, "deliveryKind":"push", "correlationSessionId":... }`；`GatewayCancelPayload { sessionId, mode: "stop" }`；`GatewayRequest` 携带 `sender/conversation/replyToMsgId` 且无 `userId` 字段
- [ ] **Step 2**：实现协议 DTO 修改 → payload 测试绿 → commit
- [ ] **Step 3**：`InboxMessage`/`InboxMetadataKeys`/`SessionData`(SessionChannelRoute)/`SessionSummary`/`FileSessionStore` 修改 + `SessionDataTests` 更新 → build `Seeing.Session` + 其测试绿 → commit
- [ ] **Step 4**：`SessionRuntime`/`ExecutionRunner`（backfill→ChannelRoute）+ `ExecutionRunnerOutboundBackfillTests` 更新（断言：首轮写 `ChannelRoute`、后续消息不覆盖）→ build 全解决方案（**此处会暴露所有仍在赋值 `InboxMessage.ChannelId/UserId` 的调用方——GatewayOrchestratorV2.SubmitAsync 在 T5/T6 改，本步先在 `GatewayOrchestratorV2.cs:60-70` 把 `ChannelId = request.ChannelId, UserId = request.UserId` 替换为 `Conversation = request.Conversation, ReplyToMsgId = request.ReplyToMsgId`，最小改动保编译**）→ 全绿 → commit `refactor: 运行时集成 ChannelRoute + 协议 v2`

---

### Task 5: 客户端双契约 + SessionEventPump + RunTracker 引用计数

**Files:**
- Create: `src/gateway/Seeing.Gateway.Client/ISessionClient.cs`
- Create: `src/gateway/Seeing.Gateway.Client/IGatewayChannelHostClient.cs`
- Create: `src/gateway/Seeing.Gateway.Client/SessionEventPump.cs`
- Create: `src/gateway/Seeing.Gateway.Client/InProcessSessionClient.cs`
- Create: `src/gateway/Seeing.Gateway.Client/RemoteSessionClient.cs`
- Move: `src/gateway/Seeing.Agent.Gateway/Core/GatewayRunTracker.cs` → `src/gateway/Seeing.Gateway.Client/GatewayRunTracker.cs`（引用计数化重写）
- Move: `src/gateway/Seeing.Agent.Gateway/Permission/GatewaySubscriptionPresenter.cs` → `src/gateway/Seeing.Gateway.Client/`（namespace 改）
- Modify: `src/gateway/Seeing.Gateway.Client/Seeing.Gateway.Client.csproj`（**新增引用 `Seeing.Session`**——`ChatRunState.Session` 持 `SessionData`）
- Modify: `src/gateway/Seeing.Gateway.Client/WebSocketGatewayClient.cs`（实现 `IGatewayChannelHostClient` 接口——类声明加接口即可，成员已齐：`ConnectAsync(channelId)`/`OnChannelOutbound`/`ResetSessionAsync`/`GetPendingPermissionsAsync`/自动重连）
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs`（改用 `SessionEventPump`；删 `StreamSessionEventsAsync`/`PumpSessionEventsAsync`/`PersistSessionAsync`/`SessionExecutionQueue` 依赖；`CancelAsync` 按帧 `mode` 分流 `InterruptAsync`/`StopAsync`——mode 读取在 T6 handler，本任务 `CancelAsync(string sessionId, string mode = "interrupt")`）
- Modify: `src/gateway/Seeing.Agent.Gateway/Hosting/GatewayHost.cs:74-91`（删 `executionQueue` 手工 new；RunTracker/Presenter 引用改 Client 包类型）
- Delete: `src/gateway/Seeing.Agent.Gateway/Core/SessionExecutionQueue.cs`
- Test: `tests/gateway/Seeing.Gateway.Client.Tests/`（新建或沿用现有 client 测试项目）+ 更新 Agent.Gateway 相关测试

**Interfaces:**
- Consumes: T2 `GatewayEventMapper.TryMap`；`ISessionRuntime`/`IPermissionSurfaceRegistry`（Abstractions，传递引用已可达）；`ISessionManager`（需 csproj 新增 `Seeing.Session` 引用）；`WebSocketGatewayClient`
- Produces:

```csharp
public interface ISessionClient
{
    Task<GatewaySubmitAckPayload> SubmitAsync(GatewayRequest request, CancellationToken ct = default);
    IAsyncEnumerable<GatewayEvent> SubscribeAsync(string sessionId, CancellationToken ct = default);
    Task<bool> CancelAsync(string sessionId, CancellationToken ct = default);          // 默认 interrupt
    Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId, string permissionId, bool allow, string? reason = null, CancellationToken ct = default);
}

public interface IGatewayChannelHostClient   // 现有 WebSocketGatewayClient 职责正式化，单连接双面
{
    string ChannelId { get; }
    bool IsConnected { get; }
    event Func<GatewayChannelOutboundPayload, CancellationToken, Task>? OnChannelOutbound;
    Task ConnectAsync(string channelId, CancellationToken ct = default);   // 连接即 channel.register v2（capabilities 由实现方提供）
    Task ResetSessionAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<GatewayPendingPermission>> GetPendingPermissionsAsync(string sessionId, CancellationToken ct = default);
}

public sealed class SessionEventPump   // 自 GatewayOrchestratorV2 提炼；InProcess 与 Orchestrator 共用
{
    public SessionEventPump(ISessionRuntime runtime, GatewayRunTracker runTracker, ILogger? logger = null);
    public IAsyncEnumerable<GatewayEvent> StreamAsync(string sessionId, CancellationToken ct);
    // 内含：呈现端注册（IPermissionSurfaceRegistry + GatewaySubscriptionPresenter）、WaitForIdle + 50ms 排空、
    // ChatEventMapper→GatewayEventMapper 映射（T2 产物）、取消→合成 Cancelled 事件、异常→合成 Error 事件
    // 不做 PersistSessionAsync（已删——运行时每轮落盘）
}
```

- `GatewayRunTracker` v2（引用计数）：`RegisterRun(sessionId)` 返回 `(string RunId, CancellationToken Token)`；`CancelRun(sessionId)` 取消该会话**全部**在途；`UnregisterRun(sessionId, runId)` 只移除自己的条目；CTS 由各订阅 try/finally Dispose
- DI 扩展：`AddInProcessSessionClient()`（Client 包内注册 `ISessionClient → InProcessSessionClient`，依赖根容器 `ISessionRuntime`）/ `AddRemoteSessionClient(GatewayClientOptions)`（`ISessionClient → RemoteSessionClient`，包装同一 `WebSocketGatewayClient` 实例）

- [ ] **Step 1**：`ISessionClient`/`IGatewayChannelHostClient`/`GatewayRunTracker` v2 完整代码 + RunTracker 引用计数单测（并发 3 订阅：互不顶掉、Unregister 只删自己、CancelRun 全取消）
- [ ] **Step 2**：`SessionEventPump` 提炼（自 orchestrator 抄逻辑，映射换 T2 产物）+ `InProcessSessionClient`（SubmitAsync→`runtime.DeliverAsync`（构造 InboxMessage：`Sender.Kind=Human`/`Sender.Id=request.Sender?.ExternalId ?? "gateway"`，`Conversation/ReplyToMsgId` 透传）；SubscribeAsync→pump；CancelAsync→`runtime.InterruptAsync`；RespondPermissionAsync→`IPermissionRequestManager.TryResolve`，需经 `IServiceProvider` scope 取）
- [ ] **Step 3**：**两实现语义等价性测试**（同事件序列 mock runtime；断言 InProcess 与 Remote(内存 WS mock) 产出相同 GatewayEvent 序列、同空闲收口、同 Cancelled 合成）
- [ ] **Step 4**：`GatewayOrchestratorV2` 切换 pump + 删队列/PersistSessionAsync + `GatewayHost` 装配更新；build 全解决方案绿；更新受影响 Agent.Gateway 测试 → commit `refactor: ISessionClient/HostClient 双契约 + SessionEventPump + RunTracker 引用计数 + 删 SessionExecutionQueue/PersistSessionAsync`

---

## ─── 审查门 ② ───

**检查清单：** ①全解决方案 build + 全量 vstest 绿 ②审查子代理抽查三处缝隙：T3 `DeliverAsync` 对 T1 契约的调用与准入顺序；T4 `ChannelRoute` 写入链（InboxMessage→metadata→ExecutionRunner backfill）首写语义未变；T5 pump 与 T2 映射器集成后 `GatewayAssistantReplyCollector` 的 `RunCompleted` 判定路径可命中（源码走查 `"loop.complete"` 恒等）

---

# Wave 3

### Task 6: Gateway Server 收窄 + 调度分发器迁移

**Files:**
- Create: `src/capabilities/Seeing.Agent.Comms/Scheduling/ChannelDeliveryScheduleDispatcher.cs`
- Delete: `src/gateway/Seeing.Agent.Gateway/Scheduling/GatewayScheduleDispatcher.cs`
- Modify: `src/gateway/Seeing.Agent.Gateway/Extensions/GatewayServiceCollectionExtensions.cs`（删 `IScheduledJobDispatcher` 单注册；`AddSeeingGatewayServer` 内部 `AddSeeingComms(registry)`）
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs:93`（Composite 固定数组 → `GetServices<IScheduledJobDispatcher>()` 枚举组合，排除 Composite 自身）
- Create: `src/gateway/Seeing.Agent.Gateway/Core/GatewayDeliveryNotifier.cs`（实现 `IChannelDeliveryNotifier`：构造 GatewayEvent → `_connectionManager.PushEvent`）
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayConnectionManager.cs`（`PushChannelOutbound` 改注册表单实例路由：经 `IChannelAdapterRegistry` 的远程代理；`RegisterChannel` 时构造代理注入注册表、`Unregister` 注销）
- Create: `src/gateway/Seeing.Agent.Gateway/Core/RemoteChannelAdapterProxy.cs`（WS 代理 `IChannelAdapter`：`SendAsync` → `PushChannelOutbound`（payload v2）→ 返回 `Delivered`（无法感知平台结果，成功=帧已投递，平台 4xx 由适配器侧记审计——**接口语义：`Delivered=true` 表示已交由通道宿主**））
- Modify: `src/gateway/Seeing.Agent.Gateway/Endpoints/GatewayWebSocketHandler.cs`（`HandleChannelRegisterAsync` 解析 v2 capabilities + 代理注入；`HandleCancelAsync` 读 `Mode` 传 orchestrator；`HandleSubmitAsync` Submit 时调 `IChannelDirectory.RegisterInbound`（从 `request.Sender/Conversation` 构造 `ChannelInboundMessage`）+ 经 `GatewayUserMessageComposer` 不变）
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewaySessionService.cs`（`ResetAsync` 顺序：注入 `ISessionRuntime` → `InterruptAsync` → **清空未读**（经 `runtime.DeliverAsync` 逐条 `Inject`+consumed？不——直接操作：`session` 的 inbox 未读标记 consumed（读 `SessionRuntime` 现有 API，若无公开 API 则新增 `ISessionRuntime.DiscardPendingAsync(sessionId)`，在 Hosting 侧实现：标记全部未读 consumed + Save）→ `ClearMessages` → 单次 `SaveAsync`）
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs`（若需新增 `DiscardPendingAsync`：lock 内标记 consumed + Save，返回条数）
- Test: 更新/新增 `tests/gateway/Seeing.Agent.Tests`（Agent.Gateway 测试）+ Comms 测试项目新增 dispatcher 测试

**Interfaces:**
- Consumes: T1/T3/T4/T5 全部产物；`ISystemReminderRenderer`、`DispatchRequest.UserId`（Abstractions/Scheduling——dispatcher 保留现有 reminder 解包逻辑）
- Produces: `ChannelDeliveryScheduleDispatcher : IScheduledJobDispatcher`（`DispatchAsync`：`EnsureSessionAsync` → reminder Inject（沿用现有信封解包 + `suppress_console_push` 判定）→ 产出 Inject（`Presentation=Assistant`）→ 读 `session.ChannelRoute` 构造 `OutboundMessage{ DeliveryKind=SystemNotice, CorrelationSessionId=request.SessionId, JobId=… }` → `OutboundDeliveryService.DeliverAsync`（Notifier 由 Comms 注入，Server 已实现为 PushEvent））；无 `ChannelRoute` → Debug 日志跳过出站（Inject 仍执行）

- [ ] **Step 1**：dispatcher 失败测试（Comms.Tests）：有 `ChannelRoute` 的会话 → `OutboundDeliveryService.DeliverAsync` 收到 `OutboundMessage`（断言 Target.Conversation 与 ChannelRoute 镜像一致、`suppress_console_push=true` 时不推）；无 `ChannelRoute` → 不投递只 Inject
- [ ] **Step 2**：实现迁移 + DI Composite 修正（Scheduler 包单测：多 dispatcher 枚举组合顺序稳定）→ commit
- [ ] **Step 3**：`GatewayDeliveryNotifier` + `RemoteChannelAdapterProxy` + ConnectionManager 注册表路由 + WS handler v2（register capabilities/取消 mode/Submit 登记）→ 单测：注册→代理可从 registry 取到；掉线（Unregister）→ registry 移除 + 多注册后注册者生效告警
- [ ] **Step 4**：`ResetAsync` 顺序 + `DiscardPendingAsync`（若新增）→ 单测：Reset 后无未读被消费、`ClearMessages` 与消费循环无竞态（并发消费中 Reset）→ build 全绿 → commit `refactor: Gateway Server 收窄 + 调度分发器迁 Comms + Notifier/代理/注册表路由 + ResetAsync 顺序修正`

---

### Task 7: QQ 适配器化（含被动窗口状态机）

**Files:**
- Create: `src/gateway/Seeing.Gateway.QQ/QQChannelAdapter.cs`（实现 `IChannelAdapter`；保留 `QQChannelPlugin : IGatewayChannelPlugin` 管理元数据不动）
- Create: `src/gateway/Seeing.Gateway.QQ/Delivery/QQPassiveWindowTracker.cs`（被动窗口状态机）
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelBridge.cs`（改用 `ISessionClient` + `IGatewayChannelHostClient`（同一 `WebSocketGatewayClient` 实例）；出站经 `QQChannelAdapter.SendAsync`；`HandleChannelOutboundAsync` 删除 sessionId 反解，改 payload v2 → `DeliveryTarget`）
- Modify: `src/gateway/Seeing.Gateway.QQ/Connection/QQHttpApiClient.cs`（`msg_seq` 键改 per-conversation（`$"{messageType}:{openId}"`）；主动模式 body 不带 `msg_id`）
- Modify: `src/gateway/Seeing.Gateway.QQ/QQSessionResolver.cs`（仅保留命名生成：`qq:group:{gid}` / `qq:direct:{uid}` / `qq:guild:{cid}` + 轮转后缀；删 Sanitize 反解耦合——命名仍需文件系统安全字符）
- Delete: `src/gateway/Seeing.Gateway.QQ/QQSessionTargetParser.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQChannelPlugin.cs`（`ConfigureServices` 增加 `IChannelAdapter` 注册：`QQChannelAdapter` 单例 + 注入 `IChannelDirectory`（命令 /subscribe 落退订状态））
- Test: `tests/gateway/Seeing.Gateway.QQ.Tests/`（新建/扩展）

**Interfaces:**
- Consumes: T1 契约；T4 payload v2/GatewayRequest v2；T5 `ISessionClient`/`IGatewayChannelHostClient`；`IChannelDirectory`
- Produces: `QQChannelAdapter : IChannelAdapter`（`SendAsync`：`DeliveryTarget`→`ParsedQQMessage` 形状（MessageType 由 `Conversation.Kind` 映射 Direct→c2c/Group→group/Guild→guild；openids 从 `Conversation.ExternalId`/`RecipientUserId`）→ `QQHttpApiClient.SendReplyAsync`；`ReplyToMsgId`→`MsgId`）；`QQPassiveWindowTracker`（`RegisterInbound(msgId, at)`、`TryAcquirePassive(msgId) → PassiveWindowResult { CanPassive, MustDropMsgId }`——窗口数值**实现时核对 QQ 官方文档**，常量集中在 `QQOptions` 新增 `PassiveWindowSeconds`/`PassiveReplyLimitPerMsg`，默认值标注「以官方文档为准，未核实前保守取 300s/2 条」）

- [ ] **Step 1**：失败测试——被动窗口状态机（登记→窗口内可被动、超窗降级 `MustDropMsgId=true`、同 MsgId 超条数降级）；适配器翻译矩阵（Direct/Group/Guild × `DeliveryTarget` 字段→API 路径/openid 选择/msg_id 有无）
- [ ] **Step 2**：实现 `QQPassiveWindowTracker` + `QQChannelAdapter` → 测试绿 → commit
- [ ] **Step 3**：桥改造（ISessionClient 替换 facade；`ProcessMessageAsync` 里 Submit 前构造 `GatewayRequest` v2（Sender/Conversation/ReplyToMsgId）+ 经 HostClient 不再直接订阅 WS handler 通道；出站路径换 adapter；命令拦截加 `/subscribe`/`/unsubscribe` → `IChannelDirectory.SetOptOutAsync`）；删 `QQSessionTargetParser`；`QQSessionResolver` 收窄 → 翻译矩阵测试（入站：C2C/GROUP_AT/AT/DIRECT 事件 → `ChannelInboundMessage` 各字段）→ build 绿 → commit `refactor: QQ 适配器化 + 被动窗口状态机 + 删 sessionId 反解`
- [ ] **Step 4**：回归测试：`GatewayAssistantReplyCollector` 集成——mock 事件流含 `loop.complete` SourceType 的 `GatewayEvent` → `RunCompleted` disposition 命中（不再依赖流自然结束兜底）

---

### Task 8: WeCom 适配器化（含流帧旁路）

**Files:**
- Create: `src/gateway/Seeing.Gateway.WeCom/WeComChannelAdapter.cs`
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComChannelBridge.cs`（同 T7 模式：ISessionClient + HostClient；出站经 adapter；删 `WeComSessionResolver` 反解职责——命名保留）
- Modify: `src/gateway/Seeing.Gateway.WeCom/Connection/WeComOutboundChannel.cs`（流帧旁路定位：被动回复窗口内 streamId/epoch 流帧直发不过投递管道；终态消息经 `WeComChannelAdapter.SendAsync` 走管道）
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComChannelPlugin.cs`（`IChannelAdapter` 注册）
- Delete: `src/gateway/Seeing.Gateway.WeCom/WeComSessionResolver.cs` 中反解部分（若整文件仅为反解则删）
- Test: `tests/gateway/Seeing.Gateway.WeCom.Tests/`（新建/扩展）

**Interfaces:**
- Consumes/Produces：与 T7 对称（`WeComChannelAdapter : IChannelAdapter`；`Conversation.Kind` 映射 WeCom 场景 Direct/Group；流帧旁路 = `WeComOutboundChannel` 既有流 API 保持，仅终态出口换管道）

- [ ] **Step 1**：翻译矩阵失败测试（WeCom 入站事件 → `ChannelInboundMessage`；`DeliveryTarget` → WeCom 出站 API 参数）
- [ ] **Step 2**：实现 adapter + 桥改造 + 流帧旁路接线 → 测试绿 → build 绿 → commit `refactor: WeCom 适配器化 + 流帧旁路`
- [ ] **Step 3**：命令拦截对齐（/new /clear /subscribe /unsubscribe 与 QQ 同集）

---

### Task 9: 工具能力包 + WebUI 出站绑定 UI

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/Seeing.Agent.Tools.ChannelMessaging.csproj`（只依赖 Abstractions）
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/ChannelMessagingModule.cs`（`ISeeingModule`；Activate 挂载三工具；DI 注册 `IChannelMessageSink` 缺省 `NoopChannelMessageSink`——若容器已有实现（Comms 已组合）则不覆盖：用 `TryAdd` 语义）
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/NoopChannelMessageSink.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/SendMessageTool.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/ListChannelUsersTool.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/ListConversationsTool.cs`
- Modify: `samples/Seeing.Agent.WebUI/Pages/SessionToolbar.razor:248-255,554-555`（出站绑定表单：两字符串 → Kind 下拉 + ExternalId + Title）
- Modify: `samples/Seeing.Agent.WebUI/Pages/SessionWindow.razor:1772-1778`（`SaveOutboundAsync` 写 `session.ChannelRoute`）
- Modify: `samples/Seeing.Agent.WebUI/Pages/SessionsPage.razor:188-191,240-245`（列表 Channel 列 + 编辑弹窗同步）
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`（`AddSeeingModule<ChannelMessagingModule>` 加组合；确认 `AddSeeingComms` 经 `AddSeeingGatewayServer` 已内部组合）
- Modify: `Seeing.Agent.slnx`
- Test: `tests/capabilities/Seeing.Agent.Tools.ChannelMessaging.Tests/`

**Interfaces:**
- Consumes: T1 `IChannelMessageSink`/`IChannelDirectory`（**注意**：能力包只依赖 Abstractions——`IChannelDirectory` 也在 Abstractions，DI 注入即可）
- Produces: 三工具（`ITool` 实现，构造注入 `IChannelMessageSink`/`IChannelDirectory`；工具 Id：`send_channel_message`/`list_channel_users`/`list_channel_conversations`；`SendMessageTool` 参数 schema：`channelId`(string,必填)/`targetUserId`(string,二选一)/`conversationExternalId`+`conversationKind`(string: direct|group|guild,二选一)/`text`(string,必填)；返回文本含成功或 `FailureKind` 中文释义——`NotInDirectory`→「该用户尚未与机器人建立对话，无法推送」等）

- [ ] **Step 1**：失败测试——`SendMessageTool`：mock Sink 返回各 `FailureKind` → 工具输出对应文案；`NoopChannelMessageSink` → 「通道投递未配置」；`ListChannelUsersTool` 分页透传
- [ ] **Step 2**：实现三工具 + Noop + 模块（Activate 挂载；schema 手写 JSON——参照 `src/capabilities/Seeing.Agent.Tools.Git` 既有 `ITool` 实现样式）→ 测试绿 → commit
- [ ] **Step 3**：WebUI 三处 UI 改造（表单读写 `ChannelRoute`；`SessionSummary.ChannelId` 投影同步）→ `dotnet build samples/Seeing.Agent.WebUI` 绿 → commit `feat: ChannelMessaging 工具包 + WebUI 出站绑定结构化`

---

## ─── 审查门 ③ ───

**检查清单：** ①全解决方案 build + 全量 vstest 绿 ②集成回归：调度推送双场景测试（群会话→群、私聊会话→私聊、`/new` 轮转后仍命中——T6/T7 产物联动）；桥 `RunCompleted` 回归绿；AdminEndpoints 保留端点（`/api/gateway/health`、`/api/admin/channels/connected`）存在性断言 ③审查子代理抽查：T6 WS handler 对 T4 payload v2 的解析完整（capabilities/mode/RegisterInbound）；T7/T8 与 T5 双客户端面的单连接事实（桥代码无第二条连接）

---

# Wave 4

### Task 10: 端到端双拓扑联调 + 文档

**Files:**
- Create: `tests/gateway/Seeing.Agent.Comms.IntegrationTests/`（e2e：进程内拓扑——headless 组合 Comms + 进程内 mock 适配器，无 WS；进程外拓扑——内存 WS mock Gateway Server + HostClient 注册 + payload v2 投递 + 掉线重试）
- Create: `docs/architecture/17-channel-comms-release-notes.md`
- Modify: `docs/architecture/02-modules.md`（模块地图：Comms/适配器/双契约）
- Modify: `AGENTS.md`（WHERE TO LOOK：通联子系统/出站投递/适配器条目；已知边界更新）
- Modify: `docs/architecture/README.md`（索引 17 号）

**Interfaces:**
- Consumes: 全部产物
- Produces: e2e 绿 + 文档

- [ ] **Step 1**：进程内拓扑 e2e：`AddSeeingComms` + 内存 `IChannelAdapter` → 工具 Sink 发送 → adapter 收到 `OutboundMessage`（断言准入链路完整经过目录登记）
- [ ] **Step 2**：进程外拓扑 e2e：内存 WS server mock（复用 `GatewayWebSocketHandler` 真实类 + `WebSocketGatewayClient` 真实类 over in-memory WS）→ register v2 → 投递 → 掉线 → `ChannelUnavailable` + 重试队列再投递
- [ ] **Step 3**：旧 bug 场景回归闭环：模拟 QQ 群消息 + c2c 消息各建会话 + `/new` 轮转 + 调度推送 → 断言各自命中来源对话、轮转不破坏路由
- [ ] **Step 4**：文档三件 + AGENTS.md 更新 → commit `docs: architecture 17 号 Release Notes + 模块地图/AGENTS.md 更新`
- [ ] **Step 5**：spec 覆盖核对（对照 spec v3.1 逐节打勾）→ 最终 commit

---

## ─── 审查门 ④ ───

**检查清单：** ①e2e 双拓扑全绿 ②spec v3.1 逐节覆盖核对表完成（审查子代理执行：spec 每节 → 计划任务/代码落点映射）③文档链接有效、AGENTS.md 与 02 模块地图一致 ④git log 提交序列干净

---

## Self-Review 记录

- **Spec 覆盖**：spec v3.1 全部节 → T1(领域模型)/T2(事件模型+映射器)/T3(Comms)/T4(协议v2+路由绑定)/T5(ISessionClient+泵+RunTracker+删队列+删Persist)/T6(Server收窄+调度迁移+Notifier+Reset顺序+Composite)/T7(QQ+被动窗口)/T8(WeCom+流帧)/T9(工具+WebUI UI+配置)/T10(测试策略+文档)。边界项核对：`channel.register` v2→T6；`cancel.mode`→T4(payload)+T5(orchestrator)+T6(handler)；保留端点→T6 不动 AdminEndpoints/Gateways health + 门③断言；单连接双面→T5(HostClient)+T7/T8 复用；`IChannelDeliveryNotifier`→T1(契约)+T3(no-op)+T6(Server 实现)；数据文件→T3；看门狗不做→无需任务 ✅
- **文件冲突矩阵**（同波互斥已核对）：W1: T1(新增 Channels/)∥T2(Mapping/Agent.Gateway mapper+orchestrator using)——orchestrator 的 using 改动与 T1 无交集 ✅；W2: T3(新包)/T4(Runtime+Session+协议 DTO+orchestrator 60-70 行最小改)/T5(Client 包+orchestrator 主体+GatewayHost)——**orchestrator 在 T4/T5 双触**：T4 仅动 `:60-70` InboxMessage 构造行，T5 动类主体/删除方法——同波并行有合并冲突风险 → **调度约束：T4 与 T5 若并行派发，orchestrator 冲突行由 T5 先行、T4 的最小改并入 T5 任务 prompt（T4 不再触 orchestrator，由 T5 一并完成该 3 行替换）**；W3: T6(Agent.Gateway+Scheduler+Comms/Scheduling)/T7(QQ 包)/T8(WeCom 包)/T9(新工具包+WebUI)——互斥 ✅
- **类型一致性**：`ChannelInboundMessage.AttachmentParts` 在 T1 为 `object`、T4 定型为 `GatewayContentPart`——T4 任务 prompt 已含此改签步骤；`ConversationInfo.ToSessionRoute()` 在 T4 定义、T6 dispatcher 消费 ✅；`GatewayRunTracker.RegisterRun` 返回形状变更已同步 T5 产出的 pump 接口 ✅
