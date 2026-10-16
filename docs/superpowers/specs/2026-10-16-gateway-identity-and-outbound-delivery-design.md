# Gateway 定位重构与通联子系统设计

**日期**: 2026-10-16
**状态**: v3 已确认（v2 + 双子代理审查修订：WebUI 侧事实修正 + 架构侧适配点补全）

## 问题

1. **定位纠缠**：Gateway 是四合一嵌合体——远程 API 服务器（WS 端点）、身份/寻址（寄生在会话字段与 sessionId 字符串编码）、出站投递（仅调度器一条特例路径）、通道桥（QQ/WeCom，绑死进程外）。身份与投递是平台级关注点，寄生在「宿主」里导致层别矛盾。
2. **路由脆弱**：路由信息编码进 sessionId 再反解；`/new` 轮转后私聊/非共享群主动推送解析错位。
3. **无身份模型**：`SessionData.UserId`/`ChannelId` 首写固定；群共享会话下是任意首个发言者；协议 `UserId` 无消费方。
4. **Agent 无主动触达能力**：无工具、无「已知用户」目录。
5. **拓扑错位**：通道桥必须进程外（ChannelHost）；QQ/WeCom 桥的 WS 客户端面与宿主/服务器职责未分层。
6. WeCom 通道同病。
7. **现行 bug（审查发现）**：`ChatEventMapper` 硬编码 `SourceType="loop_complete"` ≠ `MessageEventType.LoopComplete="loop.complete"`，桥的 `GatewayAssistantReplyCollector` RunCompleted 判定从未命中（靠订阅流自然结束兜底掩盖）；QQ 调度推送经空 `msg_id` 主动发送，行为存疑。

## 决策（已确认）

| 决策点 | 结论 |
|---|---|
| Gateway 定位 | 拆解：通联子系统（能力层模块）+ Gateway Server（远程 API 宿主）+ ChannelHost（通道进程宿主） |
| 宿主公共层 | 不新建宿主框架——公共层 = 契约端口集合 |
| 会话客户端 | **ISessionClient 纳入 v1**，采纳对象 = **QQ/WeCom 桥（进程外）与未来远程宿主**；WebUI 与 Server 同进程 DI 组合，无需迁移 |
| 客户端事件模型 | 维持 `GatewayEvent`（防腐层 + 契约策展 + 语言中立），`IMessageEvent` 不替代 |
| 会话与身份关系 | 对话隔离 + 用户身份层 |
| 主动推送准入 | 目录即授权 + 退订 + 频控 |
| v1 范围 | QQ + WeCom 同步迁移；不考虑旧格式兼容 |
| WebUI 组合形态（v3） | **v1 WebUI 保持「Server + Comms」组合不变**（管理页天然依赖 Server）；进程内拓扑 v1 目标宿主 = headless（Gateway.Server）/嵌入宿主，WebUI 双形态留待后续 |
| cancel 帧语义（v3） | `cancel` 帧增加 `mode: interrupt|stop`（默认 `interrupt`）；**该 mode 仅作用于 WS 协议面（消费方 = 桥/远程宿主）**；WebUI 停止按钮走进程内路径（`SessionRuntime` 直调），不经 cancel 帧、语义不变 |

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

- **依赖方向**：Gateway Server / ChannelHost / WebUI → Comms → Abstractions + `Seeing.Session`（原语，`ISessionManager` 在原语层——Comms 显式引用）。Gateway Server 对通联的贡献仅为「把注册到自己的远程 channel host 包装成进程外 `IChannelAdapter` 代理注入注册表」。
- **WebUI 现状澄清**：WebUI 经 `AddSeeingGatewayServer` 与 Server **同进程组合**，页面直接注入 `IGatewayServer`（无 WS 客户端）；`GatewayClientSupervisor` 是 ChannelHost 子进程监督器（依赖 `ChannelHostManager`/`ChannelHostConfigStore`），与 ISessionClient 无关。

## 领域模型（契约层 `src/abstractions/Seeing.Agent.Abstractions/Channels/`，零实现纪律）

### DTO

- `ChannelUserIdentity` — `{ ChannelId, ExternalId, DisplayName? }`
- `ConversationInfo` — `{ ChannelId, ConversationKind: Direct|Group|Guild, ExternalId, Title? }`
- `DeliveryTarget` — `{ Conversation, RecipientUserId?, ReplyToMsgId? }`
- `OutboundDeliveryKind` — `Reply | Push | SystemNotice`
- `OutboundMessage` — `{ Target, DeliveryKind, Text, Source, CorrelationSessionId?, JobId? }`
- `OutboundDeliveryResult` — `{ Delivered, FailureKind?: NotInDirectory|OptedOut|RateLimited|ChannelUnavailable|PlatformError, Detail? }`
- `ChannelInboundMessage` — 入站结构化消息（Sender/Conversation/ReplyToMsgId + 内容 + 命令标记）
- `ChannelAdapterCapabilities` — 适配器能力声明（支持的 ConversationKind、媒体类型、是否支持流式）——`channel.register` v2 携带

### 端口（接口后缀职责规范）

| 端口 | 后缀 | 职责 |
|---|---|---|
| `IChannelDirectoryStore` | Store | 纯存取：用户/对话/成员关系/退订状态/lastSeen |
| `IChannelDirectory` | Manager | 入站登记、查询、退订管理、频控计数 |
| `IChannelMessageSink` | Sink | agent 工具发送消息的唯一单向出口 |
| `IChannelAdapter` | Provider | 入站事件→`ChannelInboundMessage`；出站 `DeliveryTarget`→平台 API |
| `IChannelAdapterRegistry` | Registry | 适配器注册/查询；**单实例 per channel 语义**（重复注册：后注册者生效 + 告警日志，取消现状广播语义） |
| `IChannelDeliveryNotifier` | Sink | 投递完成/失败的单向通知出口（默认 no-op；Gateway Server 实现以推送 WS 订阅端，保持调度注入消息可见性） |

## 通联子系统（新包 `src/capabilities/Seeing.Agent.Comms/`）

依赖 Abstractions + `Seeing.Session`（原语），`AddSeeingComms(registry)` 组合：

- `ChannelDirectoryService : IChannelDirectory`（singleton）：
  - 登记入口 = 提交方显式调用 `IChannelDirectory.RegisterInbound(ChannelInboundMessage)`（编排器 / 进程内宿主在 Submit 时调用）；Comms 不订阅会话事件（对齐 docs 16 能力包纪律）
  - 持久化 `~/.seeing/comms/directory.json`（原子写）
  - 频控：内存滑窗，默认 20 条/用户/小时（`CommsOptions.RateLimitPerHour`，0 关闭）
- `OutboundDeliveryService`（singleton）`DeliverAsync(OutboundMessage)`：
  1. 目标解析（conversation → channel；userId → 其 Direct 对话）
  2. 准入强制（目录即授权 / opt-out / 频控）
  3. 模式选择（`ReplyToMsgId` 窗口内 → 被动；否则主动）——窗口判定下沉到适配器（见「QQ 被动窗口状态机」）
  4. 投递：经 `IChannelAdapterRegistry`（进程内直调 / 远程代理）
  5. 投递记录：内存重试队列（指数退避 ≤3 次）+ 审计 `~/.seeing/comms/outbound-log.jsonl`
- `ChannelDeliveryScheduleDispatcher : IScheduledJobDispatcher`（自 `GatewayScheduleDispatcher` **重构迁入**，非搬家）：
  - 剥离三依赖：`GatewayConnectionManager`（宿主）→ 出站改走 `OutboundDeliveryService`；`GatewayChannelOutboundPayload`/`GatewayEvent`（协议层，能力包不可依赖）→ 改 `OutboundMessage`/目录端口
  - **「调度注入消息推送 WS 订阅连接」行为保留（v3.1 机制修订）**：`SessionRuntime.DeliverAsync` 的 **Inject 分支只落盘、不产生任何流事件**（现状可见性来自旧 dispatcher 手工构造 GatewayEvent 直推）——故 Comms 定义通知端口 `IChannelDeliveryNotifier`（`Abstractions/Channels/`，单向出口：投递完成后通知宿主呈现端；默认 no-op），Gateway Server 实现之：收到通知 → 构造 `GatewayEvent` 经 `PushEvent` 推 WS 订阅连接。依赖方向干净（Server 实现 Comms 端口），行为与现状等价
  - `suppress_console_push` 元数据契约随迁（不得丢失）
  - **DI 组合修正**：注册进 Scheduler 的 `CompositeScheduleDispatcher`（现状 Gateway 注册直接覆盖 Composite 是既有缺陷，迁移时一并修正；注意 Composite 现为固定数组构造，需改为枚举注册）
- `IChannelMessageSink` 实现：薄封装投递管道 + 目录查询

## Gateway Server（`src/gateway/Seeing.Agent.Gateway/`）收窄为远程 API 宿主

- 职责：WS/HTTP 端点、`GatewayConnectionManager`、远程适配器代理（channel host 注册 → 构造 WS 代理 `IChannelAdapter` 注入注册表；掉线注销）、**调度注入消息的 WS 订阅转发**（补 Comms 迁出后的 `PushEvent` 缺口）
- **保留端点清单**（WebUI 管理功能依赖，不得移除）：`/api/gateway/health`（GatewayPage 判活）、`/api/admin/channels/connected`（客户端卡片判活）、`/api/admin/status|sessions|jobs`
- 组合关系：`AddSeeingGatewayServer` 内部组合 `AddSeeingComms`
- 删除：调度分发器（迁 Comms）、sessionId 反解依赖

## 通道适配器与「客户端面」分层（QQ / WeCom）

### 适配器（实现 `IChannelAdapter`）

- 入站：平台事件 → `ChannelInboundMessage`；sessionId 仅为命名（`qq:group:{gid}` 等），不再被反解；`QQSessionTargetParser`/`QQSessionResolver`/`WeComSessionResolver` 反解职责删除
- 出站：`DeliveryTarget` → 平台 API；被动/主动选择；平台特性降级保留
- 命令拦截（`/new` `/clear` `/unsubscribe` `/subscribe`）在适配器入站侧；`/unsubscribe`/`/subscribe` 经 `IChannelDirectory` 落状态
- 托管形态：进程内（headless/嵌入宿主直接组合适配器包）或进程外（ChannelHost）

### 客户端面分层（v3 修订——审查发现的接口缺口）

进程外适配器对 WS 客户端的需要分两个正交面，**不复用 ISessionClient 替代一切**：

| 面 | 契约 | 内容 |
|---|---|---|
| 会话驱动面 | `ISessionClient`（见下节） | Submit / Subscribe / Cancel / RespondPermission |
| 适配器宿主面 | **`IGatewayChannelHostClient`**（自 `WebSocketGatewayClient` 现有职责正式化：连接即 `channel.register`、`OnChannelOutbound` 出站接收、自动重连、`ResetSessionAsync`、`GetPendingPermissionsAsync`） | 桥/适配器的进程外宿主职责 |

QQ/WeCom 桥组合两者。**连接拓扑（v3.1）**：单连接双面——`IGatewayChannelHostClient` 即 `WebSocketGatewayClient` 正式化，`RemoteSessionClient` 包装**同一实例**（沿现有 `WebSocketGatewayClientFacade` 模式），桥共享单条 WS 连接，不双开。

### 管理面插件契约（v3 修订）

`IGatewayChannelPlugin`（ChannelId/OptionsType/OptionsSectionName/GetConfigSchema/ConfigFormComponentType）+ `GatewayChannelRegistry` DLL 扫描 + `.seeing/gateway-clients/*.json` 配置存储**整体保留**，归属 gateway 家族（与 `IChannelAdapter` 正交：适配器包同时实现两者——`IChannelAdapter` 是行为契约，plugin 是管理元数据契约）。WebUI 客户端管理页**无需改动**（判活走 `/api/admin/channels/connected`，端点语义不变，管理页不接触 register 帧）。

### channel.register v2（v3 新增）

payload 扩展：`{ channel, capabilities: ChannelAdapterCapabilities }`；Server 据此构造远程代理并注入注册表。`PushChannelOutbound` 广播语义改为**注册表单实例路由**（多 host 同 channel：后注册者生效 + 告警）。

### QQ 被动窗口状态机（v3 新增——现状完全缺失）

- 入站登记：MsgId → { 接收时间, 已被动回复次数 }（内存，TTL 清理）
- 窗口判定：时间窗与同 MsgId 条数上限**以 QQ 官方文档为准**（spec 不硬编码未核实数字，实现时核对）
- 降级：窗口过期 → 主动模式，body **不带 msg_id**（现状「空 msg_id 主动发送」一并修正）；`msg_seq` 从「按 MsgId 键」改为 **per-conversation 序列**
- 配额/4xx → `OutboundDeliveryResult.FailureKind` 映射（`QQApiException.StatusCode` 分类：RateLimited/PlatformError）

### WeCom 流式回复（v3 修订）

流式回复帧（streamId/epoch/RespondStream）是**被动回复路径内的平台特性**：终态消息走统一投递管道（准入/频控/审计），流帧由适配器在被动窗口内旁路直发，不重复过管道；主动推送无流式。

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

- **双实现同包**：`RemoteSessionClient`（包装 `WebSocketGatewayClient`/`HttpGatewayClient`）；`InProcessSessionClient`（直调 `ISessionRuntime` + 复用合并后映射器）
- **采纳对象（v3 修正）**：QQ/WeCom 桥的会话驱动面（替换 `WebSocketGatewayClientFacade`）+ 未来远程宿主；**WebUI 不迁移**（同进程 DI 直调，本无 WS 客户端）
- **SessionEventPump 提炼**（自 `GatewayOrchestratorV2`）：Client 包**新增对原语 `Seeing.Session` 的引用**（`ChatRunState.Session` 持 `SessionData`）；`GatewayRunTracker`（引用计数化后）与 `GatewaySubscriptionPresenter` 随泵落 Client 包共享；`GatewayOrchestratorV2` 改为复用泵
- **边界**：只覆盖聊天驱动面；适配器宿主面归 `IGatewayChannelHostClient`；会话管理/文件/todo/admin 不进

## 事件模型决策（GatewayEvent 维持为客户端消费模型）

理由：防腐层（内部事件演化不破坏线上协议）、契约策展（精选字段集，`SessionUpdatedEvent` 刻意不映射）、语言中立（WS 面向外部接入）、双向映射不可行（有损投影无法保真）。双映射器漂移是债务——合并处置见下。

## 协议演进（`Seeing.Gateway`）

- `GatewayRequest` v2：新增 `Sender: ChannelUserIdentity`、`Conversation: ConversationInfo`、`ReplyToMsgId`；**一次性删除**裸 `UserId` 与 `InboxMessage.ChannelId/UserId`（含 `InboxMetadataKeys.ChannelId/UserId` 与 `ChatOptions` 携带；`LastNonEmptyMeta` 聚合语义沿用）
- `channel.outbound` payload v2（显式路由，见通联子系统节）
- `channel.register` v2（能力声明）
- `cancel` 帧 v2：`{ sessionId, mode: interrupt|stop }`（默认 interrupt）；`cancel.ack` 的 `Cancelled` 语义按 mode 更新

## 会话路由绑定

- `SessionData` 废弃 `ChannelId`/`UserId`（first-write-wins 改结构化 `ChannelRoute`，绑定语义仍为空才写）
- **波及面（v3 补全）**：WebUI 三处出站绑定 UI 需结构化重构——`SessionToolbar.razor`（绑定表单）、`SessionWindow.razor`（`SaveOutboundAsync` 直写两字段）、`SessionsPage.razor`（列表 Channel 列 + 编辑弹窗）；`SessionSummary` 投影字段；`FileSessionStore` 读取默认值；测试 `ExecutionRunnerOutboundBackfillTests`/`GatewayScheduleDispatcherOutboundTests`/`SessionDataTests` 同步更新
- 旧会话文件无 `ChannelRoute` → 调度推送对这些会话**静默跳过并记 Debug 日志**（开发阶段可接受，显式记录）

## Agent 能力（`src/capabilities/Seeing.Agent.Tools.ChannelMessaging/`）

只依赖 Abstractions；`AddSeeingModule` + Activate 挂载：

- `SendMessageTool`（target/text/mentionUserId → `IChannelMessageSink`，返回含 `FailureKind` 的结构化结果）
- `ListChannelUsersTool` / `ListConversationsTool`
- 无 Comms/Gateway 宿主时注册 no-op sink（工具返回「通道投递未配置」）

## 配置（v3 修订）

- `CommsOptions`：节名 `Comms`，`seeing.json`，`ConfigScope.Both`，经 `ConfigSectionMeta` 注册进 `ConfigSectionRegistry`（与 ModelOverrides **同注册机制**；存储形态不同——Comms 为 seeing.json 节而非独立文件，零新机制）
- `~/.seeing/comms/directory.json` / `outbound-log.jsonl` 为运行时数据文件，**不进** ConfigSectionRegistry
- **v1 无 WebUI Comms 管理卡片**（频控/退订/审计配置走 seeing.json 手编）；管理卡片列为后续演进
- ChannelHost 进程托管层**不新增崩溃看门狗**（现有状态记录 + WS 自动重连 + 投递重试队列兜底），列为后续演进

## 数据流

```
被动回复: 用户消息 → 适配器翻译 → Submit(登记目录+DeliverAsync inbox)
          → 适配器经 ISessionClient 订阅会话事件 → 适配器直接回复(被动窗口/流帧旁路)
调度推送: Scheduler → Composite → ChannelDeliveryScheduleDispatcher → OutboundDeliveryService
          (session.ChannelRoute) → IChannelAdapterRegistry → 适配器 → 平台
          （投递后经 IChannelDeliveryNotifier 通知 Server → PushEvent 推 WS 订阅端，
            保持调度注入消息对桥/远程客户端可见——WebUI 走内部事件总线不受影响）
工具推送: agent 工具 → IChannelMessageSink → OutboundDeliveryService
          (目录解析+准入+频控) → IChannelAdapterRegistry → 适配器 → 平台
进程内拓扑: headless/嵌入宿主 → Comms(直调适配器)     无 WS、无 Gateway Server
进程外拓扑: 宿主 → Comms → Gateway Server(WS代理) → ChannelHost → 平台
WebUI(v1): 保持 Server + Comms 同进程组合（管理页依赖不变）
```

## 错误处理与边界

- 平台 4xx：投递记录标记失败；工具收到 `OutboundDeliveryResult`；审计留痕
- 退订：`/unsubscribe` → opt-out；入站登记**不清除** opt-out，恢复仅经 `/subscribe`
- 频控默认 20 条/用户/小时；超限拒绝 + `RateLimited`
- 适配器不可用：`ChannelUnavailable` → 重试队列 + 告警；重试耗尽落审计
- 群共享会话：`ChannelRoute` 绑定群对话，调度推送回群；创建者私聊推送由工具显式指定 target（不做隐式扇出）
- ISessionClient 两实现语义等价性为强契约，测试锁定

## 架构债收敛（v3 修订）

| 债务 | 处置 |
|---|---|
| `SessionExecutionQueue` | 删除。论证：现键 `(channel,session)` 跨 channel 本就不互斥、WebUI 直调路径从无队列——从不提供完备串行；删除后正确性依据 = runtime 消费循环 gate + `SessionData` 锁内编辑，行为与 WebUI 路径拉平 |
| `GatewayRunTracker` 单订阅覆盖 | 引用计数化：键含订阅身份；`CancelRun(sessionId)` 取消全部在途订阅；CTS Dispose 归各订阅 try/finally |
| cancel 用 `StopAsync` | cancel 帧 `mode` 字段（默认 interrupt，WS 消费方）；WebUI 进程内停止路径独立、语义不变 |
| `GatewaySessionService.ResetAsync` | **顺序修正（v3）**：`InterruptAsync` → **清空/标记未读 consumed**（Interrupt 不停消费循环，防新轮竞态）→ `ClearMessages` 等清理 → 单次 `SaveAsync`；`ResetSessionAsync` 适配器调用方语义不变 |
| 双事件映射器 | **以协议层 `GatewayEventMapper` 为基准**，补 `CommandResult`/`Navigate`/`SkillContent`/`LoopCancelled`；跳过未知；三处行为差异列入回归清单（LoopComplete 状态恒 Completed、StreamComplete 的 tool/assistant Object 分流——顺带修复现状 tool 消息误入 assistant 文本、`FilterThinking` 默认值显式定）；**修复 `loop_complete` ≠ `loop.complete` 现行 bug**；`ISessionClient.InProcess` 与 `GatewayOrchestratorV2` 复用之 |
| `GatewayOrchestratorV2.PersistSessionAsync` | 删除。验证：执行路径自身完整落盘（ExecutionRunner 预执行/事件/取消/终态 Save+Flush），订阅泵 finally 的 Save 只是幂等兜底 |
| ~~WebUI loopback WS~~ | **移除（v3）**——事实不存在：WebUI 同进程 DI 组合；真实对象是桥的 WS 客户端面 → 归 `ISessionClient` + `IGatewayChannelHostClient` 分层 |

## 测试策略

- `OutboundDeliveryService`：目标解析 / 目录即授权 / opt-out / 频控 / 被动-主动选择 / 重试与审计
- `ChannelDirectoryService`：登记条目 + 成员关系 + lastSeen；退订/恢复；opt-out 不被入站清除
- QQ 被动窗口状态机：登记 / 过期降级（body 不带 msg_id、msg_seq per-conversation）/ 4xx 分类映射
- 适配器翻译：QQ（群/私聊/频道 × 入站/出站/命令拦截）、WeCom 同矩阵 + 流帧旁路
- `ISessionClient`：两实现语义等价性（同一事件序列、空闲收口、取消、权限回传）
- 映射器合并回归：桥 collector RunCompleted 命中（`loop.complete`）、三处行为差异（回归对象 = 桥 collector 与未来远程客户端；**WebUI 不消费 GatewayEvent，主聊天流零影响**）
- 端到端：进程内拓扑（headless）与进程外拓扑（ChannelHost 注册 v2 + 掉线重试 + 多注册告警）
- 回归：调度推送至群/私聊（含 `/new` 轮转后）；WebUI 出站绑定 UI 结构化改造；AdminEndpoints 保留端点

## 实施顺序（v3，供 writing-plans 展开）

1. 契约层：`Abstractions/Channels/` DTO + 端口 + `ChannelInboundMessage` + inbox metadata 结构化替代
2. 映射器合并（协议层基准 + 补四类型 + 修 `loop.complete` bug）——后续步骤的地基
3. 通联子系统包 `Seeing.Agent.Comms`（目录 / 投递管道 / 注册表 / 调度分发器重构迁入含 DI Composite 修正 / Sink 实现）
4. 协议 v2（`GatewayRequest` / `channel.outbound` / `channel.register` / `cancel.mode`）+ `SessionData.ChannelRoute`（含 WebUI 三处出站绑定 UI 改造）
5. ISessionClient + `IGatewayChannelHostClient` 双契约 + `SessionEventPump` 提炼（Client 包补 `Seeing.Session` 引用）+ RunTracker 引用计数化
6. Gateway Server 收窄（远程适配器代理 / 实现 `IChannelDeliveryNotifier` 保持调度注入可见性 / 保留端点清单）
7. QQ / WeCom 适配器化（`IChannelAdapter` + plugin 元数据保留 + 删反解 + QQ 被动窗口状态机 + WeCom 流帧旁路 + 命令扩展）
8. 工具能力包 + no-op sink；架构债收尾（SessionExecutionQueue 删除 / ResetAsync 顺序）
9. 测试补齐 + 文档（architecture 17 号 Release Notes + 02 模块地图更新）
