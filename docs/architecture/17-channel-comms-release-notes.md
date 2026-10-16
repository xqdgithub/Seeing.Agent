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
