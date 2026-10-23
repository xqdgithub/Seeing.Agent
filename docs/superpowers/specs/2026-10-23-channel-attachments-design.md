# 通联出站附件（文件/图片/音视频）设计

**日期**: 2026-10-23
**状态**: 待批准（方案 A；一期实现 QQ，抽象渠道无关；无兼容层、无补丁式设计）
**相关模块**: `Seeing.Agent.Abstractions`（Channels / Execution / Permissions）、`Seeing.Agent.Core`、`Seeing.Agent.Comms`、`Seeing.Gateway` / `Seeing.Gateway.Client`、`Seeing.Agent.Gateway`、`Seeing.Gateway.QQ` / `Seeing.Gateway.WeCom`、`Seeing.Agent.Tools.ChannelMessaging`、`Seeing.IO.Local`、`samples/Seeing.Gateway.Server` / `samples/Seeing.Gateway.ChannelHost`

## 1. 问题

出站链路只有文本一种形态：

1. `OutboundMessage.Text` 是 `string`；`IChannelMessageSink` 的每个方法只接收 `text`。
2. `channel.outbound` 帧把 `Text` 声明为 required；`ChannelRegisterCapabilities` 只有 `conversationKinds` / `supportsStreaming`，没有任何媒体能力声明，而且服务端 `RegisterChannel` 明确丢弃能力（`_ = capabilities`），客户端注册帧根本不带能力。
3. `send_channel_message` 只有 `text` 参数；agent 无法把工作区产物发给用户。
4. QQ 底层已有图片/视频/语音/文件出站能力（upload + `msg_type=7`），但唯一入口是「从回复文本里正则抽 `[Image:…]` / `[File:…]`」（`QQOutboundComposer`），且 `QQTextSanitizer.ExtractImages` 是第二条同类入口。这是藏在文本约定里的渠道私有协议：只有 QQ 解析、模型无法可靠触发、其他渠道完全失效。
5. 企微出站只有 `text`（`WeComMappedOutboundBody` 仅 text 字段），没有媒体上传路径。
6. 入站已经多模态（`GatewayContentPart` 含 image/file/audio），出站与入站不对称。

目标场景：agent 在任务中生成报表/图表/压缩包，知道工作区路径，调用一次工具把「简短说明 + 附件」推到当前会话对话或指定对话；用户在 QQ 里直接收到文件，而不是收到一句「已生成 xxx.pdf」。

## 2. 目标 / 非目标 / 反目标

**目标**

- 契约层出现渠道无关的「内容 = 文本 + 附件」模型；渠道通过能力声明回答「支持哪几类、多大、几条、能否吃回复锚点」。
- agent 工具可以把**会话工作区内**的文件作为附件随消息发出；目标缺省为会话绑定的对话（子会话回溯父会话）。
- QQ 工具发送复用被动回复窗口（锚点）；平台额度不足时降级为主动发送，工具侧看到结构化失败原因。
- 进程外拓扑（Gateway Server + ChannelHost）端到端可用，附件与能力都能跨进程传递。
- QQ 是一期唯一实现；WeCom 显式声明不支持附件，工具得到可读的拒绝原因。
- 出站媒体只有一条结构化路径，不存在渠道私有的文本标记协议。

**非目标（一期不做）**

- 企微媒体出站实现（上传接口 / `media_id`）。
- 远端 URL、内联 data、跨机文件传输：一期附件唯一来源是会话工作区内的本地文件。
- 进程内适配器注册（现状未接通，本期不实现；并删除 DI 侧那条从不被消费的 `IChannelAdapter` 注册）。
- 「普通回复自动带附件」：桥的终态回复只发文本；附件只能由工具显式发送。
- WebUI 的附件发送 UI、调度任务的附件、附件重试队列、附件审计。
- 终态回复（`QQChannelBridge.SendVisibleAsync`）的失败可见性改造：沿用现有「记 Warning」行为，本期不改。
- 附件级权限审批（见 §12 的取舍）。

**反目标**

- 不为 QQ/企微定义渠道私有附件类型。
- 不把附件信息塞进 `Text`（文本标记）作为任何输入路径。
- 不在能力不足时静默丢弃附件。
- 不留旧重载、默认接口实现、可选参数默认值、兼容字段。

## 3. 约束

- **依赖方向**：契约进 `Abstractions`；投递编排进 `Comms`；工具包只依赖 `Abstractions`（+ 模块 `io.local` seam）；QQ/WeCom 实现在各自 gateway 包；协议包只依赖 `Abstractions`；Server 组合 Comms 与协议。
- **Abstractions 零实现纪律**：只放接口 / DTO / 枚举 / 常量 / 纯函数扩展方法（已有 `ConversationInfoExtensions` 先例）；派生查询一律扩展方法，DTO 不带业务方法。新增的 `ChannelCapabilitiesExtensions` 同步登记进 `docs/architecture/06` 的纯函数豁免清单。
- **工具 I/O 纪律**：工具禁止 `File.*` / `Directory.*`，必须经 `IFileSystem`（`docs/architecture/02`）。适配器不属于工具层，仍用 `File.*`（ChannelHost 未组合 `io.local`）。
- **工具描述与 Schema**：遵守 `docs/architecture/04` §9（R1–R9），嵌套属性与 `items` 逐项有描述、离散值用 `enum` 且描述逐字列出取值。
- **安全边界**：附件路径必须无条件落在**会话工作区（会话 cwd ∪ 项目根）**内；不查权限扩权白名单，不受 `Workspace.RestrictToWorkspace` 影响。
- **无兼容负担**：仓库内所有调用点同步改造；破坏性变更写入 17 号 Release Notes。

## 4. 方案对比（已选定 A）

| 方案 | 做法 | 取舍 |
|---|---|---|
| **A 结构化内容 + 能力声明（选定）** | 出站模型改为「文本 + 附件」；适配器声明能力；协议携带内容与能力；工具接附件参数 | 一处模型、全链路可传、工具可降级 |
| B 统一富内容模型 | `OutboundMessage.Parts` 复用入站 `GatewayContentPart`，文本/媒体交错 | 表达力过剩；改所有构造点与测试；QQ/企微语义只是「一条文本 + 若干媒体」 |
| C 旁路媒体端口 | 新增 `IChannelMediaSender` + 独立工具，直接调适配器 | 出站分裂成两条路径；调度与终态回复带不了附件；进程外还要新增协议帧 |

## 5. 领域模型（`Abstractions/Channels/`）

### 5.1 内容与附件（`ChannelAttachments.cs`）

```csharp
namespace Seeing.Agent.Abstractions.Channels;

public enum OutboundAttachmentKind { Image, Video, Audio, File }

/// <summary>出站附件：只描述事实，不描述平台。一期来源仅限会话工作区内的本地文件。</summary>
public sealed record OutboundAttachment
{
    public required OutboundAttachmentKind Kind { get; init; }

    /// <summary>本地绝对路径，已由生产端校验位于会话工作区内。</summary>
    public required string LocalPath { get; init; }

    /// <summary>文件字节数，由生产端读取。</summary>
    public required long SizeBytes { get; init; }

    /// <summary>展示名；null 时取路径文件名。</summary>
    public string? FileName { get; init; }
}

/// <summary>一条出站消息的内容：文本与附件至少一个非空。</summary>
public sealed record OutboundContent
{
    public string? Text { get; init; }
    public IReadOnlyList<OutboundAttachment> Attachments { get; init; } = [];
}
```

- 不引入 `MimeType`：一期 QQ 上传只用 `file_type` + 文件名，无消费方。
- 不引入来源种类枚举：一期唯一来源是本地文件，跨机/远端属未建模的后续演进。
- 入站 `ChatAttachment`（Base64，LLM 内容载体）与出站 `OutboundAttachment`（本地路径，平台投递载体）是两个方向的不同概念，不合并。

### 5.2 能力（`ChannelCapabilities.cs`）

```csharp
public sealed record ChannelAttachmentCapabilities
{
    public required IReadOnlyList<OutboundAttachmentKind> Kinds { get; init; }
    public required long MaxBytes { get; init; }
    public required int MaxPerMessage { get; init; }
}

/// <summary>按对话种类覆盖附件能力。用有序条目而非字典，避免枚举键的序列化分歧。</summary>
public sealed record ConversationAttachmentCapabilities
{
    public required ConversationKind Kind { get; init; }
    public required ChannelAttachmentCapabilities Capabilities { get; init; }
}

public sealed record ChannelCapabilities
{
    /// <summary>通道是否接受 ReplyToMsgId 作为被动回复锚点；false 时 Comms 不填锚点。</summary>
    public required bool SupportsReplyAnchor { get; init; }

    /// <summary>null = 该通道不支持附件。</summary>
    public ChannelAttachmentCapabilities? Attachments { get; init; }

    /// <summary>按对话种类覆盖；未命中回落到 <see cref="Attachments"/>。</summary>
    public IReadOnlyList<ConversationAttachmentCapabilities>? AttachmentsByConversationKind { get; init; }
}
```

派生查询放扩展方法（`ChannelCapabilitiesExtensions.cs`），DTO 保持纯数据：

```csharp
public static bool Supports(this ChannelAttachmentCapabilities capabilities, OutboundAttachmentKind kind);

/// <summary>按对话种类解析附件能力；通道不支持附件时返回 null。</summary>
public static ChannelAttachmentCapabilities? ResolveAttachments(
    this ChannelCapabilities capabilities, ConversationKind kind);
```

- 不保留 `ConversationKinds` / `SupportsStreaming`：当前没有任何消费方，不预埋。
- `AttachmentsByConversationKind` 若出现重复 `Kind`，`ResolveAttachments` 取**首个命中**（不抛错，条目数 ≤3，不值得加构造校验）。
- `SupportsReplyAnchor` 是 `required`（QQ/WeCom 必须显式回答，不允许新渠道静默获得「不填锚点」），且有真实消费方（Comms 决定是否填锚点）：WeCom 一期为 `false`，避免把陈旧 `msgid` 塞进 `aibot_respond_msg`。
- 不引入 `MaxBytesByKind`：QQ 各媒体类型真实上限尚未核实，无法如实声明。

### 5.3 端口变更（`ChannelPorts.cs`）

```csharp
public interface IChannelAdapter
{
    string ChannelId { get; }

    /// <summary>能力声明；所有实现必须显式提供（不支持附件时 Attachments = null）。</summary>
    ChannelCapabilities Capabilities { get; }

    Task<OutboundDeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct = default);
}

public interface IChannelMessageSink
{
    Task<OutboundDeliveryResult> SendToUserAsync(
        string channelId, string userExternalId, OutboundContent content, CancellationToken ct = default);

    Task<OutboundDeliveryResult> SendToConversationAsync(
        DeliveryTarget target, OutboundContent content, CancellationToken ct = default);
}

/// <summary>会话 → 出站目标解析（Sink 不承担路由职责）。</summary>
public interface IChannelTargetResolver
{
    Task<ChannelTargetResolution> ResolveSessionTargetAsync(string sessionId, CancellationToken ct = default);
}

public sealed record ChannelTargetResolution
{
    public DeliveryTarget? Target { get; init; }
    public OutboundFailureKind? FailureKind { get; init; }
    public string? Detail { get; init; }

    public static ChannelTargetResolution Ok(DeliveryTarget target);
    public static ChannelTargetResolution Fail(OutboundFailureKind kind, string detail);
}
```

- `Capabilities` 是抽象成员，无默认实现：新增渠道必须回答「支持什么」。
- Sink 只保留两个「目标 + 内容」入口；会话解析独立成 `IChannelTargetResolver`。
- 解析结果带失败种类：真实实现返回 `NotInDirectory`（「当前会话及父会话均未绑定通道」）；Comms 缺失时的 `NoopChannelTargetResolver` 返回 `ChannelUnavailable`（「通道投递未配置」），与 `NoopChannelMessageSink` 文案一致，不再把「未配置」误报成「未绑定」。
- 子会话回溯：`SessionData.ChannelRoute` 为空 → `ISessionGroupManager.GetParentAsync` 向上，最多 8 层；空/不存在的 `sessionId` 直接返回 `NotInDirectory`（不创建会话）。Direct 目标补 `RecipientUserId`（与调度分发器一致）。
- DI 注册顺序：`ChannelMessagingModule` 用 `TryAddSingleton<IChannelTargetResolver, NoopChannelTargetResolver>()`；`AddSeeingComms` 用 `AddSingleton<IChannelTargetResolver, SessionChannelTargetResolver>()`（后注册覆盖，与现有 `IChannelMessageSink` 的注册方式一致）。

### 5.4 回复锚点（并入目录）

工具在服务端运行，拿不到桥进程的入站 `msg_id`；若不带 `ReplyToMsgId`，QQ 会把「说明 + 附件」当主动消息。锚点属于「入站登记」的自然产物，并入 `IChannelDirectory`：

```csharp
public interface IChannelDirectory
{
    // …现有成员不变…

    /// <summary>登记入站；消息带 ReplyToMsgId 时同时刷新该对话的回复锚点。</summary>
    void RegisterInbound(ChannelInboundMessage message);

    /// <summary>该对话最近一条入站消息 ID；从未登记返回 null。有效性由适配器判定。</summary>
    string? ResolveReplyAnchor(ConversationInfo conversation);
}
```

- 实现 `ChannelDirectoryService`：`Dictionary<string, string>`，键复用 `ChannelKeys.Conversation`，每对话只保留最后一条；**不做 TTL**（避免与适配器窗口形成第二套时间语义）。
- 写入点跟随现有 `RegisterInbound` 调用（进程外：`GatewayWebSocketHandler.RegisterInbound`；进程内：适配器 `RegisterDirectoryInbound`）。本期验收只覆盖服务端路径。
- 读取点：`OutboundDeliveryService.DeliverAsync`——先解析适配器，仅当 `adapter.Capabilities.SupportsReplyAnchor`、`DeliveryKind == Reply` 且 `Target.ReplyToMsgId` 为空时填入；调度推送 `SystemNotice` 不取锚点。
- 有效性由适配器判定：QQ 的 `QQPassiveWindowTracker` 对未知/超窗/超额 `msg_id` 返回 `MustDropMsgId` → 降级为主动发送。
- 写入顺序：`GatewayWebSocketHandler.HandleSubmitAsync` 中 `RegisterInbound(request)` 必须在 `SubmitAsync`（唤醒消费循环）之前。

### 5.5 消息与失败模型（`ChannelModels.cs`）

```csharp
public sealed record OutboundMessage
{
    public required DeliveryTarget Target { get; init; }
    public required OutboundDeliveryKind DeliveryKind { get; init; }
    public required OutboundContent Content { get; init; }
    public string? Source { get; init; }
    public string? CorrelationSessionId { get; init; }
    public string? JobId { get; init; }
}

public enum OutboundFailureKind
{
    NotInDirectory, OptedOut, RateLimited, ChannelUnavailable, PlatformError,
    AttachmentUnsupported,   // 通道不支持附件 / 该类型 / 超过单条数量
    AttachmentTooLarge,      // 超过通道单文件上限
    AttachmentUnavailable    // 路径不存在 / 是目录 / 空文件 / 不可读
}
```

- 不变式：`Content.Text` 与 `Content.Attachments` 至少一个非空；投递入口对违反者抛 `ArgumentException`（生产端缺陷，fail fast）。
- **裁决点唯一**：Comms 按能力做唯一裁决；适配器只以**同一份能力值**做读取时的安全断言，可能产生相同的 `AttachmentTooLarge`，不构成第二套策略。

## 6. 协议演进（`Seeing.Gateway`）

```csharp
public record GatewayChannelOutboundPayload
{
    public required string Channel { get; init; }
    public ConversationInfo? Conversation { get; init; }
    public string? RecipientUserId { get; init; }
    public OutboundDeliveryKind? DeliveryKind { get; init; }
    public string? ReplyToMsgId { get; init; }
    public required string CorrelationSessionId { get; init; }
    public required OutboundContent Content { get; init; }     // 取代 Text
    public string? Source { get; init; }
    public string? JobId { get; init; }
    public string? UserId { get; init; }
}

public record GatewayChannelRegisterPayload
{
    public required string Channel { get; init; }
    public required ChannelCapabilities Capabilities { get; init; }   // 取代可空 ChannelRegisterCapabilities
}
```

- `ChannelRegisterCapabilities` 删除；`GatewayConnectedPayload` 不改（没有消费方的能力清单不预埋）。
- 序列化继续用 `GatewayWsFrameSerializer.JsonOptions`；`AttachmentsByConversationKind` 是有序条目列表，不涉及枚举字典键。

### 能力声明进注册帧的完整链路

| 位置 | 改动 |
|---|---|
| `IChannelBridge` | 新增 `ChannelCapabilities Capabilities { get; }`；删除 `IGatewayConnection? GatewayConnection => null` 的默认实现（两个桥都已显式提供） |
| `QQChannelBridge` / `WeComChannelBridge` | `Capabilities` 返回各自适配器的 `Capabilities` |
| `IGatewayConnection.ConnectAsync` | 改为 `ConnectAsync(string channelId, ChannelCapabilities capabilities, CancellationToken ct = default)`（不保留旧签名） |
| `IGatewayChannelHostClient.ConnectAsync` | 同上签名（`WebSocketGatewayClient` 一次实现两个接口） |
| `WebSocketGatewayClient` | 保存能力；`TrySendChannelRegisterAsync` 写入注册帧；重连重发同一份；`EnsureConnectedAsync` 内部调用同步改签名 |
| `GatewayConnectionManager.RegisterChannel` | 参数去掉可空/默认值；保存能力并构造 `RemoteChannelAdapterProxy(channelId, capabilities, push)` |
| `GatewayWebSocketHandler` | 反序列化 `catch (JsonException)` **且** 校验 `payload.Capabilities is null` → 回 `error`（`code = invalid_capabilities`）、不注册、不断连接 |
| `samples/Seeing.Gateway.ChannelHost` | `ConnectAsync(_bridge.ChannelId, _bridge.Capabilities, ct)` |

## 7. 投递管道（`Seeing.Agent.Comms`）

`OutboundDeliveryService.DeliverAsync`（在现有「准入通过」之后）：

0. **适配器**：`registry.TryGet` 命中；未注册 → `ChannelUnavailable`。
1. **锚点**：`DeliveryKind == Reply && adapter.Capabilities.SupportsReplyAnchor && string.IsNullOrEmpty(Target.ReplyToMsgId)` → `IChannelDirectory.ResolveReplyAnchor(conversation)`。
2. **附件校验（仅在 `Content.Attachments is { Count: > 0 }` 时执行）**：
   - `caps = adapter.Capabilities.ResolveAttachments(conversation.Kind)`；`caps is null` → `AttachmentUnsupported`（detail「通道 X 不支持附件」）；
   - `attachments.Count > caps.MaxPerMessage` → `AttachmentUnsupported`（detail 含上限）；
   - 逐项：`!caps.Supports(kind)` → `AttachmentUnsupported`；`SizeBytes <= 0` → `AttachmentUnavailable`；`SizeBytes > caps.MaxBytes` → `AttachmentTooLarge`。
   - **内容无附件时跳过本步**；因此能力为 `null` 的通道（如 WeCom）纯文本投递不受影响。
3. 校验失败**不调用适配器**，也不发文本（原子拒绝）。
4. 通过后交给适配器；平台侧失败由适配器返回。

- Comms 不做文件系统 I/O：大小由生产端写入 `SizeBytes`，Comms 只按能力裁决。
- `ChannelMessageSink`：两个方法用 `OutboundContent` 组装 `OutboundMessage`，`DeliveryKind = Reply`。
- `ChannelDeliveryScheduleDispatcher`：`Content = new OutboundContent { Text = request.Content }`，`DeliveryKind = SystemNotice` 不变（不取锚点）。
- 新增 `SessionChannelTargetResolver : IChannelTargetResolver`（Comms 注册），负责父链回溯与 Direct 补 `RecipientUserId`。
- `ChannelDeliveryMessages`：`NotInDirectory` 优先使用 `result.Detail`（与 `PlatformError` 一致）；成功文案改为「已提交发送」。Comms 的三条准入 detail 即最终面向模型的文案：「用户不在目录中」「对话不在目录中」「缺少投递用户」；目标解析的 detail 为「当前会话及父会话均未绑定通道」。
- `GatewayDeliveryNotifier`：`Text = message.Content.Text`（纯附件消息 `Text` 为 null，通知端不构造占位文本；调度消息必然带文本）。

## 8. 工具层（`Seeing.Agent.Tools.ChannelMessaging`）

**扩展 `send_channel_message`，不新增兄弟工具**：单一出站入口；新增工具会造成目标参数重复与路由歧义。

### 依赖（基础设施端口必需；通道端口由模块提供显式 Noop）

- **必需**：`IWorkspacePathGate`、`ISessionWorkspaceResolver`、`IWorkspaceProvider`（由 `AddSeeingCore` 注册）与 `IFileSystem`（由 `io.local` 模块提供，模块 `DependsOn = ["io.local"]`）——缺失即 DI 失败，禁止「依赖缺失就跳过校验」。
- **模块提供**：`IChannelMessageSink`、`IChannelTargetResolver`——`ChannelMessagingModule` 注册显式 Noop（返回 `ChannelUnavailable`「通道投递未配置」），`AddSeeingComms` 后注册覆盖。二者都返回结构化失败，绝不静默放行。

### Description（R1–R9）

```
向已登记的通道对话发送文本和文件。
- 目标三选一：targetUserId；conversationExternalId + conversationKind；都省略时发到当前会话绑定的对话。
- 只给 channelId 而不给目标会报参数错误；要发到当前会话就省略 channelId。
- 附件只接受工作区内的文件；path 写绝对路径，或相对当前工作目录的路径。
- 支持的类型、单文件上限与单条数量由目标通道能力决定，超出时返回明确失败原因。
- 查目标用 `list_channel_users` / `list_channel_conversations`。
```

首句 17 字；正文 < 700；术语为「当前会话 / 工作目录」；兄弟工具反引号点名。

### ParametersSchema（完整，可直接机械校验）

```json
{
  "type": "object",
  "properties": {
    "channelId": {
      "type": "string",
      "description": "通道 id，如 qq。省略时取当前会话绑定的通道（含父会话回溯）；显式目标也允许省略，解析不到时返回目标解析失败（未绑定通道或通道未配置）。不要只给 channelId"
    },
    "targetUserId": {
      "type": "string",
      "description": "目标用户的 ExternalId。与 conversationExternalId/conversationKind 二选一；都省略时发到当前会话绑定的对话"
    },
    "conversationExternalId": {
      "type": "string",
      "description": "目标对话的 ExternalId。必须与 conversationKind 同时提供"
    },
    "conversationKind": {
      "type": "string",
      "enum": ["direct", "group", "guild"],
      "description": "对话种类：direct 私聊、group 群、guild 频道"
    },
    "text": {
      "type": "string",
      "description": "消息正文。与 attachments 至少提供一个"
    },
    "attachments": {
      "type": "array",
      "description": "附件列表。每项必须是工作区内的文件；类型由 kind 或扩展名判定，数量与大小上限由目标通道能力决定",
      "items": {
        "type": "object",
        "description": "单个附件：本地路径，以及可选的类型与展示名",
        "properties": {
          "path": { "type": "string", "description": "绝对路径，或相对当前工作目录的路径；必须位于工作区内" },
          "kind": {
            "type": "string",
            "enum": ["image", "video", "audio", "file"],
            "description": "附件类型：image 图片、video 视频、audio 音频、file 其他文件；省略时按扩展名推断"
          },
          "name": { "type": "string", "description": "展示文件名；省略时取路径的文件名" }
        },
        "required": ["path"]
      }
    }
  },
  "required": []
}
```

`required` 为空是因为目标可回落到会话绑定、正文可只给附件；参数自洽规则见下文「参数自洽」。`channelId` 单独提供（没有任何目标参数）属参数错误，不在 Schema 层强制。

### 路径处理（`ChannelAttachmentNormalizer`，internal）

1. 基准目录 = `ISessionWorkspaceResolver.ResolveWorkingDirectory(sessionId)` ?? `IWorkspaceProvider.GetProjectRoot()`；都不可用 → 参数错误「缺少工作区，请提供绝对路径」。
2. `full = IFileSystem.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path))`。
3. `IWorkspacePathGate.EnsureWithinWorkspace(sessionId, full)` 非 null → `AttachmentUnavailable`。
4. `IFileSystem.Exists(full)` 为 false / `IsDirectory(full)` 为 true / `GetFileLength(full) <= 0` / 读取异常 → `AttachmentUnavailable`。
5. `kind` 显式值优先，缺省按扩展名推断；`name` 缺省取文件名；填充 `SizeBytes`。
6. 工具不做类型/数量/上限判定（唯一权威是 Comms）。

### 支撑变更

- `IWorkspacePathGate` 新增 `string? EnsureWithinWorkspace(string sessionId, string path)`：**严格工作区包含**（会话 cwd ∪ 项目根），不查 `IPermissionGrantStore`、不读 `RestrictToWorkspace`；与 `EnsureAllowed` 共享私有路径判定核心，XML 注释写明两者差异（条件 / 无条件）。`AllowAllGate`、`AllowAllPathGate` 两个测试替身同步实现。
- `IFileSystem` 新增 `long GetFileLength(string path)` 与 `bool IsDirectory(string path)`：工具需要文件大小/目录判定且禁止 `File.*`。语义：`GetFileLength` 对目录或缺失文件抛 `IOException`，工具统一映射为 `AttachmentUnavailable`；`IsDirectory` 对缺失路径返回 false。`LocalFileSystem` 与测试替身（`InMemoryFileSystem`、`GhostEntryFileSystem`）同步实现。
- `ChannelMessagingModule.DependsOn = ["io.local"]`，并注册 `NoopChannelTargetResolver`。

### 参数自洽（参数错误）

- `targetUserId` 与 `conversationExternalId`/`conversationKind` 互斥；`conversationExternalId` 与 `conversationKind` 必须成对。
- `text` 与 `attachments` 至少一个；`attachments: []` 视为未提供。
- **只给 `channelId` 而不给任何目标参数** → 参数错误「已指定 channelId 但未指定目标对话；请给出 targetUserId / conversationExternalId+conversationKind，或省略 channelId 以发送到当前会话」。

参数错误是工具层返回的 `ToolResult`（`Success = false`、`Title = "参数错误"`），不是 `OutboundDeliveryResult`（`ToolResult` 无 `Failure` 静态工厂）。

### 目标解析（失败原样透传，不折叠成参数错误）

1. **显式目标**（`targetUserId` 或 `conversationExternalId + conversationKind`）：`channelId` 显式值优先；未给时调用 `IChannelTargetResolver` **只取** `Conversation.ChannelId`（含父链回溯）。resolver 失败 → **原样返回其 `FailureKind/Detail`**（Noop → `ChannelUnavailable`「通道投递未配置」；真实实现 → `NotInDirectory`「当前会话及父会话均未绑定通道」），并在 detail 末尾追加「；请提供 channelId」（不提示省略目标参数，避免模型丢掉用户指定的目标）；`FailureKind` 不变，不折叠成参数错误。
2. **目标全空**：调用 `IChannelTargetResolver` 取完整 `DeliveryTarget` → `SendToConversationAsync`；失败原样返回 `FailureKind/Detail`（detail 已说明「当前会话及父会话均未绑定通道」，不再追加提示）。

工具输出：成功「已提交发送」+ 目标 + 附件名；失败按 `FailureKind` 输出可读文案（`ChannelDeliveryMessages` 新增三类）。

## 9. QQ 适配器实现（`Seeing.Gateway.QQ`）

### 能力声明（构造时固化）

`QQChannelAdapter` 构造注入 `IOptions<QQOptions>`（`QQServiceCollectionExtensions` 的显式 `new QQChannelAdapter(...)` 同步改），构造时固化：

```csharp
_capabilities = new ChannelCapabilities
{
    SupportsReplyAnchor = true,
    Attachments = new()
    {
        Kinds = [Image, Video, Audio, File],
        MaxBytes = _options.MaxMediaBytes,     // 默认 10 MB
        MaxPerMessage = 5
    },
    AttachmentsByConversationKind =
    [
        new() { Kind = ConversationKind.Guild, Capabilities = new()
        {
            Kinds = [Image], MaxBytes = _options.MaxMediaBytes, MaxPerMessage = 5
        }}
    ]
};
```

### 发送编排（文本与附件共用一套逐条计数）

适配器内新增私有 `SendContentAsync(ParsedQQMessage target, OutboundContent content, CancellationToken ct)`：

1. 文本（`content.Text` 非空）→ `QQTextSanitizer.SplitChunks` 分块；**每块** `ApplyPassiveWindow` 得到 `prepared`，再调 `_http.SendTextAsync(prepared, chunk, cancellationToken: ct)`。
2. 附件 → 逐项 `ApplyPassiveWindow` 得到 `prepared`，再调 `_http.SendAttachmentAsync(prepared, attachment, ct)`。
3. 每条平台消息独立 `TryAcquirePassive`；拒绝（未知/超窗/超额）→ `ApplyPassiveWindow` 返回的 `prepared.MsgId` 为空（主动模式）。
4. 纯附件消息完全跳过文本分支（不注入「(空回复)」占位）。
5. 聚合结果：全部成功 `Ok()`；部分失败 `Fail(PlatformError, "已发送 n/m；失败项：…")`；未发送任何内容 → 首个失败结果原样返回。

**`msg_id` 唯一来源**：`ApplyPassiveWindow` 写入的 `ParsedQQMessage.MsgId`。**适配器面**的两个入口（`SendTextAsync` 调用点、`SendAttachmentAsync`）不接收 `msgId` 参数；底层 `SendMediaMessageAsync` 没有 `ParsedQQMessage`，保留 `msgId` 形参（调用方传 `prepared.MsgId`）；`SendGuildImageFileAsync` **删除 `msgId` 形参**，改用 `target.MsgId`。URL 场景的 `SendGuildImageAsync` 无调用点，随旧链路删除。

`SendAsync` = `ToParsed(message.Target)` + `SendContentAsync`。**删除 `Translate(DeliveryTarget, tracker)`**：它在去掉 `Prepare` 后与 `ToParsed` 等价，保留即死参数。

文本失败在现有实现里是异常而非结果：`SendContentAsync` 对**每块**先 `catch (OperationCanceledException) when (ct.IsCancellationRequested) → throw`（用户取消必须传播，且停止后续项），再 `catch (QQApiException)` 与 `catch (Exception ex)`（handler 首行 `ct.ThrowIfCancellationRequested()`：取消期间抛出的非 OCE 异常统一归一为取消；超时等非用户取消的 `OperationCanceledException` 走失败聚合）→ 映射为 `OutboundDeliveryResult.Fail` 后参与聚合，避免文本失败整次抛出而丢失「附件已发送」的部分语义。

### HTTP 客户端契约（逐项、可回传失败）

```csharp
public Task<OutboundDeliveryResult> SendAttachmentAsync(
    ParsedQQMessage target, OutboundAttachment attachment, CancellationToken ct = default);
```

- 复用既有 `OutboundDeliveryResult`（`Ok()` / `Fail(kind, detail)`），不再定义同构的渠道私有结果类型。
- `msg_id` 只取自 `target.MsgId`（由 `ApplyPassiveWindow` 写入），适配器面入口不接收 `msgId` 参数。
- **能力预检在适配器完成**：`QQHttpApiClient` 不持有 `ChannelCapabilities`，只负责读文件与上传；`FileInfo.Length` 预检与「按目标对话种类解析 `_capabilities`」都在适配器侧执行（避免为了拿能力而让 HTTP 客户端反向依赖适配器）。超出 → `AttachmentTooLarge`；文件缺失/不可读 → `AttachmentUnavailable`。
- 平台异常按 `QQApiException` 映射（429 → `RateLimited`，其余 → `PlatformError`）。映射函数放到 `Connection` 下的 internal helper，供 HTTP 客户端与适配器共用，避免同程序集内反向依赖；`QQChannelAdapterTests` 的两个 `MapApiFailure_*` 用例随迁。
- `c2c`/`group`：upload → `SendMediaMessageAsync(msg_type=7)`；`guild`：`SendAttachmentAsync` 的 guild 分支按 `attachment.Kind` 拒绝非图片（返回 `AttachmentUnsupported`），图片走 `SendGuildImageFileAsync`（`msg_id` 取 `target.MsgId`）。
- 部分失败聚合时，detail 统一带附件名：`"{FileName ?? LocalPath}: {Detail}"`，便于定位到具体文件。
- `ReadSourceAsBase64Async` / `SendGuildImageFileAsync` 里现有的 `_options.MaxMediaBytes` 断言**保留为读取期安全断言**（与能力声明同一配置值，防 TOCTOU：预检后文件被替换/增长）；不构成第二套策略。

### 终态回复路径（不删空）

`QQChannelAdapter.SendParsedAsync(ParsedQQMessage target, string text, ct)` 保留（桥的 11 处 `SendVisibleAsync` 依赖它），内部改为：

- 直接 `SendContentAsync(target, new OutboundContent { Text = text }, ct)` 的**文本分支**（分块 + Markdown/URL 回落 + 逐块 acquire）；
- 空文本直接返回 `Ok()`（不发送）；桥的 `SendVisibleAsync` 已在上层兜底「(空回复)」——XML 注释写明「空文本视为已处理、不发送」；
- 不再解析文本里的媒体标记，也不再追加 guild 媒体备注。
- 不再经过 `Translate`（`SendParsedAsync` 的入参已是 `ParsedQQMessage`）。

桥与 `SendVisibleAsync` 不需要改动语义。

### 删除渠道私有文本媒体协议（整条链路）

| 删除项 | 位置 |
|---|---|
| `QQOutboundComposer` 整文件（`QQOutboundMediaKind` / `QQOutboundMediaItem` / `QQOutboundPayload` / `Compose` / `ResolveMediaType` / `IsHttpUrl` / `IsDataUrl` / `IsLocalFile`） | `QQOutboundComposer.cs` + `QQOutboundComposerTests` |
| `QQTextSanitizer.ExtractImages` | `QQTextSanitizer.cs` + 对应用例 |
| `SendReplyAsync` | 由 `SendContentAsync` + `SendTextAsync` 取代 |
| `SendImagesAsync` / `SendMediaItemsAsync` / `SendGuildImageAsync` / `UploadMediaAsync` | 无外部调用点（`SendGuildImageAsync` 仅被待删的 `SendMediaItemsAsync` 调用），删除 |
| `QQOutboundMediaItem` 参数的 upload 私有方法 | 改为 `(string localPath, string? fileName, int mediaType, …)` |

媒体类型映射（`OutboundAttachmentKind → file_type`，`.silk/.amr → 3`）以 internal static helper 留在 `QQHttpApiClient` 内部；不再对外暴露 QQ 私有媒体类型。删除后文本中的 `[File:…]` / `![..](..)` 按普通文本发送。

### 被动额度

- 同一 `msg_id` 的总消耗次数 = ack(1) + 文本块(n) + 附件(m) + 权限卡片(k) + 终态回复块(j)；ack 由 `Task.Run` 并发发出，实际顺序不确定，因此只保证**计数正确**，不承诺发送顺序。
- **决策：`QQOptions.PassiveReplyLimitPerMsg` 默认值由 2 改为 5**，并同步 `QQOptions` 的 XML summary 与 `[Display]` 文案（当前仍写「保守取 2 条」）。旗舰场景 ack + 说明 + 1 附件 + 终态回复 = 4，留 1 次余量。
- 实施第一步按 QQ 官方文档核对被动回复上限：若官方上限小于 5，改回实际值，并把 §2 的「复用被动窗口」表述为「尽力复用」。设计的正确性不依赖该数字——逐条计数 + 耗尽降级。
- 额度耗尽只降级为主动消息，不阻断；平台拒绝主动消息时返回 `PlatformError` 且工具可见。

## 10. 其他实现

- **WeCom**：`Capabilities => new() { SupportsReplyAnchor = false, Attachments = null }`。`SendAsync` 取 `message.Content.Text`；附件在 Comms 层被拒；不填锚点，沿用现有主动/被动判断。
  - **已知债**：WeCom 平台支持被动回复，但适配器的 `_inboundFrames` 无失效判定，无法安全消费外部传入的陈旧 `ReplyToMsgId`，因此一期不声明锚点能力。补上「未知/过期 msgid → 主动发送」后可将 `SupportsReplyAnchor` 置 `true`。
- **RemoteChannelAdapterProxy**：构造 `(string channelId, ChannelCapabilities capabilities, Func<GatewayChannelOutboundPayload, bool> push)`；`Capabilities` 返回注册帧能力；`SendAsync` 写 `Content = message.Content`。
- **测试替身**：`MemoryChannelAdapter`、Moq 用法必须显式提供 `Capabilities`。
- **删除死注册**：`QQChannelPlugin` / `WeComChannelPlugin` 里的 `services.AddSingleton<IChannelAdapter>(...)` 无任何消费方（桥依赖具体适配器类型），删除。

## 11. 破坏性变更清单（无兼容层）

**契约 / 协议**

| 变更 | 影响文件 |
|---|---|
| `OutboundMessage.Text` → `required OutboundContent Content` | `ChannelModels.cs`；构造点：`ChannelMessageSink`×2、`ChannelDeliveryScheduleDispatcher`、`QQChannelBridge`、`WeComChannelBridge`；读点：`GatewayDeliveryNotifier`（`Content.Text`） |
| `IChannelMessageSink` 两方法改为 `OutboundContent`；新增 `IChannelTargetResolver` + `ChannelTargetResolution` | `ChannelPorts.cs`、`ChannelMessageSink`、`NoopChannelMessageSink`、`SendMessageTool`、新增 `NoopChannelTargetResolver` |
| `IChannelAdapter.Capabilities` 抽象成员 | `QQChannelAdapter`、`WeComChannelAdapter`、`RemoteChannelAdapterProxy`、`tests/gateway/Seeing.Agent.Comms.IntegrationTests/Support/CommsIntegrationFixture.cs`（`MemoryChannelAdapter`） |
| `IChannelDirectory.ResolveReplyAnchor` + 锚点写入 | `ChannelDirectoryService`、目录测试替身 |
| `IWorkspacePathGate.EnsureWithinWorkspace` | `WorkspacePathGate`、`tests/capabilities/Seeing.Agent.Files.Tests/AllowAllGate.cs`、`tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/AllowAllPathGate.cs`、`WorkspacePathGateSessionRootTests` |
| `IFileSystem.GetFileLength` / `IsDirectory` | `Seeing.IO.Local/LocalFileSystem`、`ReadToolFileSystemTests.InMemoryFileSystem`、`Seeing.Agent.Files.Tests/GhostEntryFileSystem` |
| 三个新 `OutboundFailureKind` | `ChannelDeliveryMessages`（含 `NotInDirectory` 改用 Detail、成功文案「已提交发送」）、工具文案 |
| `GatewayChannelOutboundPayload.Text` → `required OutboundContent Content` | `PayloadV2SerializationTests`、`GatewayInboundParserTests`、`QQChannelBridge`、`WeComChannelBridge`、`RemoteChannelAdapterProxy` |
| `ChannelRegisterCapabilities` 删除；注册帧 `required ChannelCapabilities` + 显式 null 校验 | `GatewayWsFrame.cs`、`GatewayConnectionManager`、`GatewayWebSocketHandler`、`GatewayChannelRoutingTests` |
| `IGatewayConnection.ConnectAsync` / `IGatewayChannelHostClient.ConnectAsync` 改签名 | `WebSocketGatewayClient`（`ConnectAsync`/`EnsureConnectedAsync`/`TrySendChannelRegisterAsync`）、`ChannelHost/Program.cs`、`WebSocketGatewayClientTests`、`RemoteSessionClientSequentialExchangeTests` |
| `IChannelBridge`：新增 `Capabilities`、删除 `GatewayConnection` 默认实现 | `IChannelBridge`、`QQChannelBridge`、`WeComChannelBridge` |
| `RemoteChannelAdapterProxy` 构造函数 | `GatewayConnectionManager`、`GatewayChannelRoutingTests` |

**QQ（整条旧媒体链路）**

| 删除/变更 | 文件 |
|---|---|
| 删除 `QQOutboundComposer.cs` 整文件与测试 | `QQOutboundComposer.cs`、`QQOutboundComposerTests` |
| 删除 `QQTextSanitizer.ExtractImages` 与用例 | `QQTextSanitizer.cs`、`QQTextSanitizerTests` |
| 删除 `SendReplyAsync` / `SendImagesAsync` / `SendMediaItemsAsync` / `SendGuildImageAsync` / `UploadMediaAsync`；`SendGuildImageFileAsync` 去掉 `msgId` 形参（改用 `target.MsgId`）；新增 `SendAttachmentAsync` | `QQHttpApiClient.cs`、`QQChannelAdapter.cs` |
| 适配器注入 `IOptions<QQOptions>`、固化能力、`SendAsync` 改编排、`SendParsedAsync` 改文本分支、删除 `Translate` | `QQChannelAdapter.cs`、`Extensions/QQServiceCollectionExtensions.cs`（显式 `new`） |
| `PassiveReplyLimitPerMsg` 默认 2 → 5，同步 XML/`[Display]` 文案 | `QQOptions.cs` |
| 删除 `IChannelAdapter` DI 死注册 | `QQChannelPlugin.cs`、`WeComChannelPlugin.cs` |
| 桥映射 `Content` | `QQChannelBridge.cs`、`WeComChannelBridge.cs` |

**宿主 / 样例**

| 变更 | 文件 |
|---|---|
| 挂载 ChannelMessaging 模块 + 增加项目引用 | `samples/Seeing.Gateway.Server/Program.cs`、`samples/Seeing.Gateway.Server/Seeing.Gateway.Server.csproj` |
| ChannelHost 传能力 | `samples/Seeing.Gateway.ChannelHost/Program.cs` |

**测试**：`ChannelModelsTests`、`OutboundDeliveryServiceTests`、`ChannelMessageSinkTests`、目录与锚点测试、`ChannelMessagingToolTests`、`CommsIntegrationFixture`、`InProcessGroupDeliveryTests`、`ChannelDeliveryScheduleDispatcherTests`、`GatewayChannelRoutingTests`、`PayloadV2SerializationTests`、`GatewayInboundParserTests`、`WebSocketGatewayClientTests`、`RemoteSessionClientSequentialExchangeTests`、`ToolMetadataTests`、`WorkspacePathGateSessionRootTests`、`QQPassiveWindowTrackerTests`、`QQChannelAdapterTests`（4 个 `Translate_*` 迁移为 `ToParsed` / `ApplyPassiveWindow` 语义；2 个 `MapApiFailure_*` 随映射 helper 迁到 `Connection` helper 测试）、`QQTextSanitizerTests`、`QQOutboundComposerTests`（删除）。

> `OutboundDeliveryServiceTests` / `ChannelDeliveryScheduleDispatcherTests` 中所有 `Mock<IChannelAdapter>` 必须显式设 `Capabilities`（`SupportsReplyAnchor` 为 `required`，Moq 默认返回 null 会导致 NRE）。

## 12. 失败语义与边界

- **原子拒绝**：能力/数量/大小校验失败时不发任何内容。
- **部分送达**：适配器内部（部分附件失败）返回 `PlatformError` 并注明已发送数量与失败项，无回滚。
- **被动额度**：同一 `msg_id` 的每条携带 `msg_id` 的平台消息消耗一次（含 ack、权限卡片、终态回复）；额度耗尽降级为主动消息；平台拒绝 → `PlatformError`/`RateLimited` 且工具可见。
- **终态回复**：沿用桥现有「失败记 Warning」行为（本期不改），不计入「工具可见失败」承诺。
- **锚点**：只对 `Reply` 且 `SupportsReplyAnchor` 的通道生效；适配器负责有效性（QQ 超窗/超额/未知即降级）。WeCom 一期不声明该能力（已知债，见 §10）。
- **进程外语义**：工具返回「已提交发送」= 出站帧已交给通道宿主，不代表平台已送达；平台侧失败只记录在 ChannelHost 日志（本期无回执帧）。
- **同机假设**：`LocalPath` 由适配器进程直接读取；跨机 ChannelHost 不在本期范围。
- **安全边界**：附件路径必须通过 `EnsureWithinWorkspace`（严格工作区、无条件）+ 目录准入 + 能力上限。不新增附件级权限审批：主场景是把本次任务产物发给正在对话的用户，逐次审批噪音大于收益；「读权限白名单」不等于「外发许可」，因此扩权路径不作为附件来源。
- **生产者契约**：路径解析与工作区校验由生产端（工具）执行，Comms 信任 `SizeBytes` 与 `LocalPath`；新增生产端（调度/UI 附件）必须复用同一校验（契约测试锁定）。

## 13. 配置

- 不新增配置节。QQ 上限复用 `QQOptions.MaxMediaBytes`；单条消息附件数固定 5（适配器能力声明）。
- `QQOptions.PassiveReplyLimitPerMsg` 默认值由 2 改为 5（见 §9）；实现时按官方文档复核，仅下调不上调。
- 锚点无 TTL、无配置；有效性由适配器判定。
- 工具不做上限硬编码（唯一权威是能力声明）。

## 14. 测试策略

- **契约**：`ResolveAttachments` 回落/覆盖/不支持/重复 Kind 取首个；`Supports`；`SupportsReplyAnchor` 为 required（缺省反序列化失败）；序列化往返（含有序条目列表）。
- **投递**：不支持/超数量/超大小/SizeBytes 非正 拒绝矩阵；**`Attachments` 为 null 或空 + 纯文本 → 正常投递且不解析能力**；拒绝时不调用适配器；锚点只在 `Reply` + `SupportsReplyAnchor` 时生效、`SystemNotice` 不取锚点；频控与通知仍生效。
- **目录**：`RegisterInbound` 刷新锚点；无 `ReplyToMsgId` 不写；跨对话不误用。
- **目标解析**：会话绑定、父链回溯（≤8）、空/不存在 sessionId 返回 `NotInDirectory`、Direct 补 `RecipientUserId`；**Noop 返回 `ChannelUnavailable` 而非参数错误**；只给 `channelId` 不给目标 → 参数错误；`IChannelTargetResolver` 在两种 DI 注册顺序（模块先/Comms 先）下都解析到真实实现。
- **工具**：三选一与缺省；`channelId` 解析规则；相对路径解析；`RestrictToWorkspace=false` 时越界仍被 `EnsureWithinWorkspace` 拒绝；目录/空文件/不存在拒绝；kind 推断；`attachments: []` 视为未提供；`ToolMetadataTests` 通过。
- **QQ**：`c2c`/`group` 四类附件上传与 `msg_type=7` 请求体；`guild` 仅图片；逐条 acquire（文本分块、附件、终态回复各自计数，测试用显式 `replyLimit` 构造 tracker）；额度耗尽降级；文本块失败被映射为失败结果并参与聚合；取消时 `OperationCanceledException` 必须传播且停止后续项；逐项失败结果；上传前大小预检；旧文本标记不再触发媒体。
- **WeCom**：`Attachments = null` 时附件被拒；`SupportsReplyAnchor=false` 不填锚点。
- **协议 / 进程外**：能力经 `ConnectAsync → register → RegisterChannel → 代理` 透传；缺能力与 `capabilities: null` 均回 `invalid_capabilities` 且不断连接；出站 `Content` 往返。
- **回归**：文本投递、调度推送、终态回复。

## 15. 实施顺序

1. **契约**：`OutboundAttachment` / `OutboundContent` / `ChannelAttachmentCapabilities` / `ConversationAttachmentCapabilities` / `ChannelCapabilities` + 扩展方法（含 06 豁免登记）、`IChannelAdapter.Capabilities`、`IChannelMessageSink` 改签名、`IChannelTargetResolver` + `ChannelTargetResolution`、`IChannelDirectory.ResolveReplyAnchor`、`OutboundMessage.Content`、失败种类、`IFileSystem` 两个原语、`IWorkspacePathGate.EnsureWithinWorkspace`。
2. **协议 / 客户端**：注册帧能力（含 null 校验）、出站帧 `Content`、`IGatewayConnection` 与 `IGatewayChannelHostClient` 签名、`IChannelBridge.Capabilities`。
3. **Comms**：能力校验、锚点读写（含 `HandleSubmitAsync` 顺序修正）、Sink、`SessionChannelTargetResolver`、`ChannelDeliveryMessages`、`GatewayDeliveryNotifier`、调度分发器与 Noop 适配。
4. **工具**：Schema/Description、目标解析、路径规范化与严格包含校验、kind/name 推断、失败文案、模块 `DependsOn` 与 Noop resolver。
5. **QQ**：删除旧媒体链路、能力声明、`SendAttachmentAsync`、逐条被动额度、`SendParsedAsync` 改文本分支、桥映射。
6. **WeCom**：显式空能力 + `SupportsReplyAnchor=false`、`Content.Text`。
7. **宿主 / 样例**：`RegisterChannel` 接通、`RemoteChannelAdapterProxy`、ChannelHost 传能力、Gateway.Server 挂载 ChannelMessaging（含 csproj）、删除插件死注册。
8. **测试补齐 + 文档**（17 号 Release Notes 增补、AGENTS.md WHERE TO LOOK 一行）。

## 16. 留给实现者的决定

- `QQHttpApiClient` 内部重构粒度（是否把 upload 抽成内部 `IQQMediaUploader`）——不影响契约，按可测性选择。
- 扩展名→kind 映射表的范围（参考 `QQMediaFetcher` 现有表）。
- 日志字段与措辞。
