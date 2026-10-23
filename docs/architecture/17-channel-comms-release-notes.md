# 17 通联子系统（Channel Comms / Outbound Delivery）Release Notes

**日期：** 2026-10-16
**设计规格：** [`2026-10-16-gateway-identity-and-outbound-delivery-design.md`](../superpowers/specs/2026-10-16-gateway-identity-and-outbound-delivery-design.md)
**相关模块：** `Seeing.Agent.Abstractions`（`Channels`）、`Seeing.Agent.Comms`、`Seeing.Gateway` / `Seeing.Gateway.Client`、`Seeing.Agent.Gateway`、`Seeing.Gateway.QQ` / `Seeing.Gateway.WeCom`、`Seeing.Agent.Tools.ChannelMessaging`、`Seeing.Agent.Scheduler`、`samples/Seeing.Agent.WebUI`

---

## 1. 问题

渠道身份绑在会话 id 上：QQ `/new` 轮转 sessionId 后，调度推送按 id 反解路由，群消息和私聊会投错对话。出站、目录、频控和 Gateway 协议散落在桥与调度器里，进程内宿主与 ChannelHost 没有同一条投递管道。

## 2. 决策

- **对话是路由，sessionId 只是名字。** 入站登记 `ConversationInfo`；会话绑定 `SessionData.ChannelRoute`；出站 `DeliveryTarget` 只读对话，不解析 sessionId。
- **目录即授权。** `ChannelDirectoryService.RegisterInbound` 之后，`OutboundDeliveryService` 才把消息交给 `IChannelAdapter`。群/频道按对话准入，私聊按用户准入。
- **调度拆成两段。** `SessionScheduleDispatcher` 用 `InboxDelivery.Inject` 写回会话；`ChannelDeliveryScheduleDispatcher` 只按 `ChannelRoute` 出站，自己不 Inject。
- **双拓扑。** 进程内：宿主组合 Comms + 适配器，无 WebSocket。进程外：Gateway Server 做远程 API 宿主，QQ/WeCom 桥单连接双面（`ISessionClient` + `IGatewayChannelHostClient`）。
- **协议 v2。** `channel.outbound` 带显式对话；`channel.register` 带能力；`cancel` 帧带 `mode`（默认 interrupt）。

## 3. 破坏性变更

| 项 | 之前 | 现在 |
|----|------|------|
| `SessionData.ChannelId` / `UserId` | 会话上的裸字段，first-write-wins | **删除**。改为 `SessionChannelRoute`（`ChannelId` / `Kind` / `ExternalId` / `Title`）。旧文件没有 `ChannelRoute` 时，调度出站跳过并记 Debug |
| `GatewayRequest.UserId` | 请求上的裸用户 id | **删除**。改为 `Sender`、`Conversation`、`ReplyToMsgId` |
| `channel.outbound` payload | 由 sessionId 反解目标 | **v2**：显式 `Conversation`、`RecipientUserId`、`DeliveryKind`、`CorrelationSessionId` |
| cancel | 停止语义混在一条路径 | **`mode`**：`interrupt`（默认）或 `stop`；`cancel.ack.Cancelled` 按 mode 解释。WebUI 进程内停止路径独立 |

WebUI 出站绑定改为读写 `ChannelRoute`（工具栏、会话窗、会话列表）。`SessionSummary` 的 Channel 列来自 `ChannelRoute.ChannelId`。

## 4. 落点

| 层 | 位置 |
|----|------|
| 契约 | `src/abstractions/Seeing.Agent.Abstractions/Channels/` |
| 通联 | `src/capabilities/Seeing.Agent.Comms/`（模块 id `comms`） |
| 工具 | `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/`（模块 id `channel-messaging`） |
| 协议 / 客户端 | `src/gateway/Seeing.Gateway/`、`Seeing.Gateway.Client/` |
| Server | `src/gateway/Seeing.Agent.Gateway/`；保留 `/api/gateway/health`、`/api/admin/channels/connected` 以及 `/api/admin/status`、`sessions`、`jobs` |
| 适配器 | `Seeing.Gateway.QQ`、`Seeing.Gateway.WeCom` |

## 5. 已知边界

- 进程外拓扑（内存 WebSocket、`channel.register` v2、掉线后 `ChannelUnavailable` 再投递）没有端到端测试。
- `ChannelUnavailable` 目前直接失败返回，没有重试队列。
- `CommsOptions.AuditLogPath` 已出现在配置模型，投递管道尚未写审计文件。
- v1 没有 WebUI Comms 管理卡片；ChannelHost 不新增崩溃看门狗。

---

## 2026-10-23 出站附件（文件 / 图片 / 音视频）

**设计规格：** [`2026-10-23-channel-attachments-design.md`](../superpowers/specs/2026-10-23-channel-attachments-design.md)
**覆盖模块：** `Seeing.Agent.Abstractions`（`Channels`）、`Seeing.Agent.Comms`、`Seeing.Gateway` / `Seeing.Gateway.Client`、`Seeing.Agent.Gateway`、`Seeing.Gateway.QQ` / `Seeing.Gateway.WeCom`、`Seeing.Agent.Tools.ChannelMessaging`、`Seeing.IO.Local`、`samples/Seeing.Gateway.Server` / `ChannelHost`

出站从「只有文本」扩为「文本 + 附件」：渠道用能力声明回答支持范围，附件只走一条结构化路径，不再依赖文本媒体标记。一期只实现 QQ，WeCom 显式声明不支持附件。

### 1. 内容模型（`Abstractions/Channels/ChannelAttachments.cs`）

| 类型 | 字段 | 说明 |
|---|---|---|
| `OutboundAttachment` | `Kind` / `LocalPath` / `SizeBytes`（均 `required`）、`FileName` | 只描述磁盘事实：一期来源仅限会话工作区内的本地文件；不建模 `MimeType` 与来源种类 |
| `OutboundContent` | `Text?`、`Attachments`（默认空列表） | 一条消息的内容；**文本与附件至少一个非空**，投递入口对违反者抛 `ArgumentException` |
| `OutboundAttachmentKind` | `Image` / `Video` / `Audio` / `File` | 渠道无关的附件类型 |

入站 `ChatAttachment`（Base64，喂给 LLM）与出站 `OutboundAttachment`（本地路径，交给平台）是相反方向的两个概念，不合并。

### 2. 能力模型（`Abstractions/Channels/ChannelCapabilities.cs`）

| 类型 | 字段 |
|---|---|
| `ChannelAttachmentCapabilities` | `Kinds` / `MaxBytes` / `MaxPerMessage`（均 `required`） |
| `ConversationAttachmentCapabilities` | `Kind`、`Capabilities`（均 `required`）；用有序条目而非字典 |
| `ChannelCapabilities` | `SupportsReplyAnchor`（`required`）、`Attachments`（`null` = 该通道不支持附件）、`AttachmentsByConversationKind`（未命中回落到 `Attachments`） |

- 派生查询集中在纯函数扩展 `ChannelCapabilitiesExtensions`（`Supports` / `ResolveAttachments`），DTO 保持纯数据；已登记 `docs/architecture/06` 的纯函数豁免清单。重复 `Kind` 取**首个命中**；反序列化产生的残缺条目（`Capabilities` / `Kinds` 为 null）跳过，不抛错。
- `SupportsReplyAnchor` 为 `required`：新渠道必须显式回答，不允许静默获得「不填锚点」。QQ = `true`，WeCom 一期 = `false`。
- 不保留 `ConversationKinds` / `SupportsStreaming`：当前没有消费方，不预埋。

### 3. 协议与投递链路

- **注册帧**：`GatewayChannelRegisterPayload.Capabilities` 为 `required ChannelCapabilities`，`ChannelRegisterCapabilities` 删除。反序列化失败或 `capabilities: null` → 回 `error`（`code = invalid_capabilities`）、不注册、**不断开连接**；重连重发同一份能力。
- **连接签名**：`IGatewayConnection.ConnectAsync` / `IGatewayChannelHostClient.ConnectAsync` 改为 `ConnectAsync(string channelId, ChannelCapabilities capabilities, CancellationToken ct = default)`；`IChannelBridge.Capabilities` 暴露桥能力。
- **出站帧**：`GatewayChannelOutboundPayload.Text` → `required OutboundContent Content`；`GatewayConnectedPayload` 不变（无消费方不预埋能力清单）。
- **投递裁决（Comms）**：准入通过后解析适配器 → 补入锚点 → 有附件时按能力裁决 → 交给适配器。
  - 锚点仅在 `DeliveryKind == Reply`、`adapter.Capabilities.SupportsReplyAnchor` 且 `Target.ReplyToMsgId` 为空时取 `IChannelDirectory.ResolveReplyAnchor`；调度推送 `SystemNotice` 不取锚点，有效性由适配器判定。
  - 拒绝矩阵（**原子拒绝**：不调适配器、也不发文本）：通道不支持附件 / 类型不支持 / 超单条数量 → `AttachmentUnsupported`；`SizeBytes <= 0` → `AttachmentUnavailable`；超单文件上限 → `AttachmentTooLarge`。
  - **无附件时跳过能力解析**：`Attachments = null` 的通道纯文本投递不受影响；Comms 不做文件 I/O（大小由生产端写入 `SizeBytes`）。
- **目标解析**：会话 → 目标解析独立成 `IChannelTargetResolver`（`ChannelTargetResolution`）；子会话沿父链回溯（最多 8 层）；`NoopChannelTargetResolver` 返回 `ChannelUnavailable`「通道投递未配置」，真实实现返回 `NotInDirectory`「当前会话及父会话均未绑定通道」。
- **QQ 适配器**：能力构造时固化——`SupportsReplyAnchor = true`；四类附件、`MaxBytes = QQOptions.MaxMediaBytes`（默认 10 MB）、`MaxPerMessage = 5`；`Guild` 频道按对话种类覆盖为**仅图片**。文本与附件共用逐条被动计数；纯附件消息跳过文本分支；部分失败返回 `PlatformError` 并注明已发送数与失败项，无回滚。
- **工具**：扩展 `send_channel_message`（不新增兄弟工具）新增 `attachments` 参数；目标三选一（`targetUserId` / `conversationExternalId + conversationKind` / 缺省发到当前会话绑定对话），只给 `channelId` 而不给目标属参数错误；附件路径经 `IWorkspacePathGate.EnsureWithinWorkspace` 做**严格工作区包含**校验（不看 `RestrictToWorkspace`、不查权限白名单），类型缺省按扩展名推断。

### 4. 破坏性变更（无兼容层）

| 项 | 变更 |
|---|---|
| `OutboundMessage.Content` | `Text`（`string`）→ `required OutboundContent Content`；纯附件消息 `Content.Text` 为 null，通知端不构造占位文本 |
| `IChannelMessageSink` | 两个方法改为接收 `OutboundContent`；会话解析移出到 `IChannelTargetResolver` |
| `IChannelAdapter.Capabilities` | 新增抽象成员（无默认实现）；测试替身与 Moq 必须显式设置（`SupportsReplyAnchor` 为 `required`） |
| `IGatewayConnection.ConnectAsync` | 增加 `channelId` / `capabilities` 参数；`IGatewayChannelHostClient` 同步 |
| 注册能力 | `ChannelRegisterCapabilities` 删除；注册帧改为 `required ChannelCapabilities` 并校验显式 null |
| QQ 文本媒体标记 | 删除 `QQOutboundComposer` 整文件与 `QQTextSanitizer.ExtractImages`；`[Image:…]` / `[File:…]` / `![]()` 按普通文本发送；删除 `SendReplyAsync` / `SendImagesAsync` / `SendMediaItemsAsync` / `SendGuildImageAsync` / `UploadMediaAsync`，新增 `SendAttachmentAsync` |
| `QQOptions.PassiveReplyLimitPerMsg` | 默认值 2 → 5；同一 `msg_id` 的 ack、文本块、附件、权限卡片、终态回复逐条计数，额度耗尽降级为主动消息 |
| 其他契约 | `IChannelDirectory.ResolveReplyAnchor`（锚点并入目录）；`OutboundFailureKind` 新增 `AttachmentUnsupported` / `AttachmentTooLarge` / `AttachmentUnavailable`；`IWorkspacePathGate.EnsureWithinWorkspace`；`IFileSystem.GetFileLength` / `IsDirectory` |

### 5. 已知边界

- **WeCom 无附件、无锚点**：`Attachments = null`（附件在 Comms 层被拒），`SupportsReplyAnchor = false`（适配器 `_inboundFrames` 无失效判定，无法安全消费外部传入的陈旧 `msg_id`）；补上「未知/过期 msgid → 主动发送」后才考虑开放锚点。
- **进程外无投递回执**：工具返回「已提交发送」只表示出站帧已交给 ChannelHost，不代表平台已送达；平台侧失败只记录在 ChannelHost 日志。
- **同机文件假设**：`LocalPath` 由适配器进程直接读取，跨机 ChannelHost 不在本期范围。
- 不做附件级权限审批（主场景是把本次任务产物发给正在对话的用户）；普通终态回复只发文本，附件只能由工具显式发送；无 WebUI 附件发送 UI、无附件重试队列与审计。
- QQ 部分附件失败返回 `PlatformError` 且工具可见；终态回复沿现有「记 Warning」行为，不计入「工具可见失败」承诺。
