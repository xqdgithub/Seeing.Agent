# Gateway 定位重构与通联子系统设计

**日期**: 2026-10-16
**状态**: v2 已确认（方案 A 投递管道 + 定位选项 2 拆解 + ISessionClient b1 纳入 v1）

## 问题

1. **定位纠缠**：Gateway 是四合一嵌合体——远程 API 服务器（WS 端点）、身份/寻址（寄生在会话字段与 sessionId 字符串编码）、出站投递（仅调度器一条特例路径）、通道桥（QQ/WeCom，绑死进程外）。身份与投递是平台级关注点，寄生在「宿主」里导致层别矛盾：调度器（Hosting 层）与 agent 工具（能力层）的出站都不得不依赖 gateway 家族。
2. **路由脆弱**：路由信息编码进 sessionId 再反解（`QQSessionResolver`/`QQSessionTargetParser`）；`/new` 轮转追加时间戳后缀后，私聊/非共享群主动推送全部解析错位。
3. **无身份模型**：`SessionData.UserId`/`ChannelId` 首写固定；群共享会话下是任意首个发言者，不能作路由目标；协议 payload 的 `UserId` 无消费方——群会话任务永远只推群。
4. **Agent 无主动触达能力**：无工具、无「已知用户」目录。
5. **拓扑错位**：WebUI 经 loopback WS 连同进程 gateway server；通道桥必须进程外（ChannelHost）。
6. WeCom 通道同病。

## 决策（已确认）

| 决策点 | 结论 |
|---|---|
| Gateway 定位 | **选项 2 拆解**：通联子系统（能力层可组合模块）+ Gateway Server（远程 API 宿主，与 WebUI 平级）+ ChannelHost（通道进程宿主） |
| 宿主公共层 | **不新建宿主框架**——公共层 = 契约端口集合（`ISessionRuntime` / `IPermissionChannel`+Surface / 组合协议 / 通联端口）；避免重蹈 `IExtension` 巨型接口覆辙 |
| 会话客户端 | **ISessionClient（b1）纳入 v1**：契约放 `Seeing.Gateway.Client`，进程内/远程双实现 |
| 客户端事件模型 | **维持 `GatewayEvent`**，不以 `IMessageEvent` 替代——GatewayEvent 是防腐层 + 契约策展（见「事件模型决策」） |
| 会话与身份关系 | 对话隔离 + 用户身份层 |
| 主动推送准入 | 目录即授权 + 退订 + 频控（投递管道强制，工具层不可绕过） |
| v1 范围 | QQ + WeCom 同步迁移；不考虑旧格式兼容（开发阶段） |

## 体系定位（四层形态）

```
┌─ 宿主层（呈现/接入形态，共享契约、无公共框架）─────────────────┐
│ WebUI   TUI   Cli  │  Gateway Server（远程API宿主）  │ ChannelHost │
│ （人类呈现宿主）     │ （会话+通联经 WS/HTTP 暴露给机器）│（通道进程宿主）│
└────┬──────────────┴──────────┬────────────────────┴──────┬─────┘
     │ 组合                    │ 组合（向注册表注入远程适配器代理）│ 适配器插件
┌────▼─────────────────────────▼─────────────────────────────▼─────┐
│ 能力/子系统层： Comms（身份·目录·投递·频控）  Scheduler  Memory …   │
└───────────────────────────────┬───────────────────────────────────┘
┌───────────────────────────────▼───────────────────────────────────┐
│ Hosting（会话入站运行时） → Core 脊柱 → Abstractions / 原语         │
└───────────────────────────────────────────────────────────────────┘
```

- **依赖方向**：Gateway Server / ChannelHost / WebUI → Comms → Abstractions。Gateway Server 对通联的贡献仅为「把注册到自己的远程 channel host 包装成进程外 `IChannelAdapter` 代理注入注册表」——接入职责，非业务归属。
- **调度分发下沉**：`GatewayScheduleDispatcher` 从 `Seeing.Agent.Gateway` 迁入 Comms（只需 `ISessionRuntime` + 投递服务 + `ISessionManager`），Gateway Server 不再承载调度分发。

## 领域模型（契约层 `src/abstractions/Seeing.Agent.Abstractions/Channels/`，零实现纪律）

### DTO

- `ChannelUserIdentity` — `{ ChannelId, ExternalId, DisplayName? }`（QQ union openid / WeCom userid）
- `ConversationInfo` — `{ ChannelId, ConversationKind: Direct|Group|Guild, ExternalId, Title? }`
- `DeliveryTarget` — `{ Conversation, RecipientUserId?（群内定向/@）, ReplyToMsgId?（被动窗口） }`
- `OutboundDeliveryKind` — `Reply | Push | SystemNotice`
- `OutboundMessage` — `{ Target, DeliveryKind, Text, Source, CorrelationSessionId?, JobId? }`
- `OutboundDeliveryResult` — `{ Delivered, FailureKind?: NotInDirectory|OptedOut|RateLimited|ChannelUnavailable|PlatformError, Detail? }`
- `ChannelInboundMessage` — 入站结构化消息（Sender/Conversation/ReplyToMsgId + 内容 + 命令标记）

### 端口（接口后缀职责规范）

| 端口 | 后缀 | 职责 |
|---|---|---|
| `IChannelDirectoryStore` | Store | 纯存取：用户/对话/成员关系/退订状态/lastSeen |
| `IChannelDirectory` | Manager | 入站登记、查询、退订管理、频控计数 |
| `IChannelMessageSink` | Sink | agent 工具发送消息的唯一单向出口 |
| `IChannelAdapter` | Provider | 通道适配器：入站事件→`ChannelInboundMessage`；出站 `DeliveryTarget`→平台 API |
| `IChannelAdapterRegistry` | Registry | 适配器注册/查询（进程内实现 + Gateway Server 注入的远程代理） |

## 通联子系统（新包 `src/capabilities/Seeing.Agent.Comms/`）

只依赖 Abstractions + 原语，`AddSeeingComms(registry)` 组合（与 Scheduler/Memory 平级）：

- `ChannelDirectoryService : IChannelDirectory`（singleton）：
  - **登记入口 = 提交方显式调用** `IChannelDirectory.RegisterInbound(ChannelInboundMessage)`（编排器 / 进程内宿主在 Submit 时调用）——Comms 不订阅会话事件（对齐 docs 16 能力包纪律），登记与 `ChannelRoute` 绑定同源（同一 `ChannelInboundMessage`），单一真相
  - 持久化 `~/.seeing/comms/directory.json`（原子写）
  - 频控：内存滑窗，默认 20 条/用户/小时（`CommsOptions.RateLimitPerHour`，0 关闭）
- `OutboundDeliveryService`（singleton）统一管道 `DeliverAsync(OutboundMessage)`：
  1. 目标解析（conversation → channel；支持 userId → 其 Direct 对话）
  2. 准入强制（目录即授权 / opt-out / 频控）
  3. 模式选择（`ReplyToMsgId` 窗口内 → 被动；否则主动，受平台配额约束）
  4. 投递：经 `IChannelAdapterRegistry` 取适配器（进程内直调 / 远程代理走 WS）
  5. 轻量投递记录：内存重试队列（指数退避 ≤3 次）+ 审计日志 `~/.seeing/comms/outbound-log.jsonl`
- `ChannelDeliveryScheduleDispatcher : IScheduledJobDispatcher`（自 `GatewayScheduleDispatcher` 迁入）：调度产出经投递管道，读 `session.ChannelRoute`
- `IChannelMessageSink` 实现：薄封装投递管道 + 目录查询（工具出口）

## Gateway Server（`src/gateway/Seeing.Agent.Gateway/`）收窄为远程 API 宿主

- 职责：WS/HTTP 端点（submit/cancel/permission/channelRegister/channelOutbound）、`GatewayConnectionManager`、**远程适配器代理**（channel host 经 `channel.register` 注册后，Server 构造 WS 代理 `IChannelAdapter` 注入 Comms 注册表；掉线即注销）
- 组合关系：`AddSeeingGatewayServer` 内部组合 `AddSeeingComms`（宿主拉子系统，方向合法）
- 删除：调度分发器（迁 Comms）、sessionId 反解依赖

## 通道适配器（QQ / WeCom，实现 `IChannelAdapter`）

- **入站**：平台事件 → `ChannelInboundMessage`（结构化 Sender/Conversation/ReplyToMsgId + 内容）；sessionId 生成保留对话隔离规则但仅为命名（`qq:group:{gid}` / `qq:direct:{uid}` / `wecom:…`），不再被反解；`QQSessionTargetParser`/`QQSessionResolver`/`WeComSessionResolver` 反解职责删除
- **出站**：`DeliveryTarget` → 平台 API；被动（msg_id/msg_seq）/主动选择；平台特性降级保留（guild 无视频、markdown 回退等）
- 托管形态：**进程内**（宿主直接组合适配器包）或**进程外**（ChannelHost 加载插件 + 经 WS 注册到 Server）——拓扑成为部署选择
- `/new` `/clear` `/unsubscribe` `/subscribe` 命令在适配器入站侧拦截

## ISessionClient（契约放 `src/gateway/Seeing.Gateway.Client/`）

```csharp
public interface ISessionClient
{
    Task<GatewaySubmitAckPayload> SubmitAsync(GatewayRequest request, CancellationToken ct = default);
    IAsyncEnumerable<GatewayEvent> SubscribeAsync(string sessionId, CancellationToken ct = default); // 订阅至会话空闲
    Task<bool> CancelAsync(string sessionId, CancellationToken ct = default);
    Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId, string permissionId, bool allow, string? reason = null, CancellationToken ct = default);
}
```

- **双实现同包**：`RemoteSessionClient`（现有 `WebSocketGatewayClient`/`HttpGatewayClient` 正式化包装）；`InProcessSessionClient`（直调 `ISessionRuntime` + 复用 `IMessageEvent → GatewayEvent` 映射器）
- **抽取工作**：把 `GatewayOrchestratorV2` 的「订阅至空闲收口」泵（WaitForIdle + 排空窗口 + 呈现端注册）提炼为可复用 `SessionEventPump`，两实现共享
- **边界（防巨型接口）**：只覆盖聊天驱动面（submit/subscribe/cancel/permission.respond）；会话管理（`ISessionManager`）、文件、todo、admin 不进——进程内宿主继续直用 DI，需要完整面的远程客户端用原生 WS 协议
- **采纳策略**：WebUI 的 `GatewayClientSupervisor` 换用 `ISessionClient`（本就是 remote 形态，近零改动）；TUI/Cli **不强制迁移**（继续进程内直调 `ISessionRuntime`）；未来远程宿主（移动端/远程 TUI）天然可用

## 事件模型决策（GatewayEvent 维持为客户端消费模型）

`IMessageEvent` 技术上可替代（需补 `[JsonPolymorphic]` 标注），但**不替代**，理由：

1. **防腐层**：`IMessageEvent` 是内部演化面（事件类型频繁增删改）；GatewayEvent 扁平 DTO 使「新增内部事件」对旧客户端无害（映射器跳过未知），替代后每次内部事件变更都是线上协议破坏性变更
2. **契约策展**：`GatewayEventData` 是精选字段集（如 `SessionUpdatedEvent` 携带整个 SessionData 被刻意不映射）；替代会把 15 个类型的完整载荷冻结为公开契约，契约面爆炸
3. **语言中立**：Gateway WS 面向外部接入，扁平 camelCase DTO 不绑定 .NET 类型层级
4. 双向映射中间路线不可行：GatewayEvent 是有损投影，反向映射无法保真 → 两实现「语义等价」契约破产

处置：双事件模型不是债务，**双映射器漂移**才是——合并为协议层单一 `GatewayEventMapper`（跳过未知 + 补 `LoopCancelledEvent`/`CommandResult`/`Navigate`/`SkillContent`），`ISessionClient.InProcess` 复用之。

## 协议演进（`Seeing.Gateway`）

- `GatewayRequest` v2：新增 `Sender: ChannelUserIdentity`、`Conversation: ConversationInfo`、`ReplyToMsgId`；废弃裸 `UserId`
- `channel.outbound` payload v2（显式路由）：`{ channel, conversation: {kind, externalId}, recipientUserId?, deliveryKind, replyToMsgId?, text, source, correlationSessionId?, jobId? }`——sessionId 降级为关联 ID
- 不新增 WS 帧类型（`ChannelInboundMessage` 经既有 submit 帧进入运行时）

## 会话路由绑定

- `SessionData` 废弃 `ChannelId`/`UserId` 首写固定字段，改结构化 `ChannelRoute`（`ConversationInfo` 快照），由 inbox metadata 在首轮写入（`InboxMetadataKeys` 扩展结构化 conversation）

## Agent 能力（`src/capabilities/Seeing.Agent.Tools.ChannelMessaging/`）

只依赖 Abstractions；`AddSeeingModule` + Activate 挂载：

- `SendMessageTool` — 参数 target（userId/conversation）、text、可选 mentionUserId；经 `IChannelMessageSink`；返回结构化结果（含失败原因：`NotInDirectory`/`OptedOut`/`RateLimited`/`PlatformError`）
- `ListChannelUsersTool` — 按最近活跃分页列出已知用户
- `ListConversationsTool` — 列出已知对话
- 无 Comms/Gateway 宿主时注册 no-op sink（工具返回「通道投递未配置」），schema 稳定

## 数据流

```
被动回复: 用户消息 → 适配器翻译 → Submit(登记目录+DeliverAsync inbox)
          → 适配器订阅会话事件（ISessionClient/WS） → 适配器直接回复(带 msg_id)
调度推送: Scheduler → ChannelDeliveryScheduleDispatcher → OutboundDeliveryService
          (session.ChannelRoute) → IChannelAdapterRegistry → 适配器 → 平台
工具推送: agent 工具 → IChannelMessageSink → OutboundDeliveryService
          (目录解析+准入+频控) → IChannelAdapterRegistry → 适配器 → 平台
进程内拓扑: 宿主 → Comms(直调适配器)          无 WS、无 Gateway Server
进程外拓扑: 宿主 → Comms → Gateway Server(WS代理) → ChannelHost → 平台
```

## 错误处理与边界

- 平台 4xx（配额/目标不存在）：投递记录标记失败；工具收到 `OutboundDeliveryResult`（agent 可转告用户）；审计留痕
- 退订：`/unsubscribe` → 目录 opt-out；入站登记**不清除** opt-out，恢复仅经 `/subscribe`（适配器拦截命令，经 `IChannelDirectory` Manager 端口落状态）
- 频控默认 20 条/用户/小时；超限拒绝 + `RateLimited`
- 适配器不可用（进程内未注册 / 远程 host 掉线）：`ChannelUnavailable` → 重试队列 + 告警；重试耗尽落审计
- 群共享会话：`ChannelRoute` 绑定群对话，调度推送回群（来源对话语义）；创建者私聊推送由工具显式指定 target（不做隐式扇出——扇出为后续演进）
- ISessionClient 两实现的语义等价性（空闲收口、取消、权限回传）为强契约，测试锁定

## 顺带收敛的架构债（本次一并完成）

| 债务 | 处置 |
|---|---|
| `SessionExecutionQueue`（Gateway 私有副本） | 删除——`DeliverAsync` 为写入+激活，运行时消费循环已保证同会话串行 |
| `GatewayRunTracker` 单订阅覆盖 | 引用计数化（同会话多订阅各持 CTS，spec §4.6/N2） |
| 网关 cancel 用 `StopAsync`（级联子会话+丢弃未读） | 改 `InterruptAsync`（硬停当前轮，继续消费） |
| `GatewaySessionService.ResetAsync` 绕过运行时直改会话 | 先 `runtime.InterruptAsync` 再清理，写回单次 Save |
| 双事件映射器（`GatewayEventMapper` vs `ChatEventMapper`，缺 `LoopCancelledEvent`） | 合并为协议层单一 `GatewayEventMapper`；`ISessionClient.InProcess` 复用之 |
| `GatewayOrchestratorV2.PersistSessionAsync` 兜底写 | 删除——运行时每轮已落盘（docs 10/13 单一写入者） |
| WebUI loopback WS 依赖具体客户端类 | 换用 `ISessionClient`（remote 实现） |

## 测试策略

- `OutboundDeliveryService`：目标解析 / 目录即授权 / opt-out / 频控 / 被动-主动选择 / 重试与审计——纯单测
- `ChannelDirectoryService`：登记 → 条目 + 成员关系 + lastSeen；退订/恢复；opt-out 不被入站清除
- 适配器翻译单测：QQ（群/私聊/频道 × 入站/出站/命令拦截）、WeCom 同矩阵
- `ISessionClient`：**两实现语义等价性测试**（同一事件序列、空闲收口、取消、权限回传），远程实现用内存 WS mock
- 端到端：进程内拓扑（宿主+Comms+进程内适配器，无 WS）与进程外拓扑（ChannelHost WS 注册 + payload v2 投递 + host 掉线重试）
- 回归：调度推送至群/私聊会话（旧 bug 场景，含 `/new` 轮转后）

## 实施顺序（供 writing-plans 展开）

1. 契约层：`Abstractions/Channels/` DTO + 端口（含 `ChannelInboundMessage`、inbox metadata 扩展）
2. 通联子系统包 `Seeing.Agent.Comms`（目录 / 投递管道 / 注册表 / 调度分发器迁入 / Sink 实现）
3. 协议 v2（`GatewayRequest` / `channel.outbound` payload / 映射器合并）
4. ISessionClient 契约 + 双实现 + `SessionEventPump` 提炼；WebUI 接入
5. Gateway Server 收窄（远程适配器代理注入注册表；删调度分发/反解）
6. QQ / WeCom 适配器化（实现 `IChannelAdapter`，删 sessionId 反解；命令拦截扩展 `/subscribe`）
7. 工具能力包 + no-op sink 注册
8. 架构债收敛（上表）+ ChannelHost 进程外拓扑验证
9. 测试补齐 + 文档（architecture 17 号 Release Notes + 02 模块地图更新）
