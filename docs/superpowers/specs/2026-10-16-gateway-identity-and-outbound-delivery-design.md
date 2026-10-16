# Gateway 身份与出站投递架构设计

**日期**: 2026-10-16
**状态**: 已确认（方案 A：服务器集中式投递，吸收方案 C 的轻量投递记录）

## 问题

现架构出站链路存在领域模型缺失，导致路由脆弱与能力缺失：

1. **路由信息编码进 sessionId 字符串再反解**（`QQSessionResolver` / `QQSessionTargetParser`）：`/new` 轮转追加 `_yyyyMMddHHmmss` 后缀后，私聊/非共享群的主动推送全部解析错位（推到不存在的 openid/group）。
2. **无身份模型**：`SessionData.UserId`/`ChannelId` 为首写固定（first-write-wins）的附带字段；群共享会话（`ShareSessionInGroup` 默认 true）下 `UserId` 是任意首个发言者，不能作为路由目标。
3. **出站只有一条特例路径**：仅调度器（`GatewayScheduleDispatcher`）推 `channel.outbound`；协议 payload 的 `UserId` 字段无消费方——QQ 桥只反解 sessionId，群会话的任务永远只推群、不推创建者私聊。
4. **Agent 无主动触达能力**：无工具、无"已知用户"目录，只能被动回复当前对话。
5. WeCom 通道同病（`WeComSessionResolver` 同模式）。

## 决策（已确认）

| 决策点 | 结论 |
|---|---|
| 会话与身份关系 | **对话隔离 + 用户身份层**：群/私聊各自独立会话上下文；新增用户级身份用于路由与目录 |
| 主动推送准入 | **目录即授权 + 退订 + 频控**：入站发现即登记；opt-out 可退订；滑窗频控兜底；准入在投递管道强制，工具层不可绕过 |
| v1 范围 | **QQ + WeCom 同步迁移**（协议层通用） |
| 兼容 | **不考虑**（开发阶段，干净替换；`QQSessionTargetParser`/`WeComSessionResolver` 反解职责直接删除） |

## 领域模型（契约层 `src/abstractions/Seeing.Agent.Abstractions/Channels/`，零实现纪律）

### DTO

- `ChannelUserIdentity` — `{ ChannelId, ExternalId, DisplayName? }`（QQ union openid / WeCom userid）
- `ConversationInfo` — `{ ChannelId, ConversationKind: Direct|Group|Guild, ExternalId, Title? }`（Direct 的 ExternalId = 用户 ExternalId；Group 的 = group_openid；Guild 的 = channel_id）
- `DeliveryTarget` — `{ Conversation: ConversationInfo, RecipientUserId?（群内定向/@）, ReplyToMsgId?（被动回复窗口内携带） }`
- `OutboundDeliveryKind` — `Reply | Push | SystemNotice`

### 端口（接口后缀职责规范）

| 端口 | 后缀 | 职责 |
|---|---|---|
| `IChannelDirectoryStore` | Store | 纯存取：用户条目 / 对话条目 / 成员关系 / 退订状态 / lastSeen |
| `IChannelDirectory` | Manager | 入站登记、查询（按用户/对话/最近活跃）、退订与恢复、频控计数 |
| `IChannelMessageSink` | Sink | agent 工具发送消息的唯一单向出口（只写，不可反向调用） |

`IChannelMessageSink` 之外，投递编排（`OutboundDeliveryService`）为 `Seeing.Agent.Gateway` 内部服务，不进 Abstractions（仅 Gateway 实现、仅 Sink 与调度器消费）。

## 协议演进（`Seeing.Gateway`）

### `GatewayRequest` v2

- 新增 `Sender: ChannelUserIdentity`、`Conversation: ConversationInfo`、`ReplyToMsgId`（结构化，桥入站时填充）。
- 废弃裸 `UserId` 字符串（`ChannelId` 保留，等价于 `Conversation.ChannelId`）。

### `channel.outbound` payload v2（显式路由）

```json
{
  "channel": "qq",
  "conversation": { "kind": "group", "externalId": "…" },
  "recipientUserId": null,
  "deliveryKind": "push",
  "replyToMsgId": null,
  "text": "…",
  "source": "scheduler.cron | tool.send_message",
  "correlationSessionId": "…",
  "jobId": null
}
```

- sessionId 降级为 `correlationSessionId`（仅关联，不承载路由）——轮转 bug 根因消除。

## 服务器端（`src/gateway/Seeing.Agent.Gateway/`）

### `ChannelDirectoryService : IChannelDirectory`（singleton）

- **登记时机**：`GatewayOrchestratorV2.SubmitAsync` 收到结构化请求时，登记用户（ExternalId + DisplayName + lastSeen）、对话（含 Title 惰性更新）、成员关系（群场景 sender ∈ group）。
- **持久化**：`~/.seeing/gateway/directory.json`（量小，JSON；原子写）。
- **频控**：内存滑窗，默认 `N 条/用户/小时`（`GatewayOptions` 可配，0 = 关闭）。

### `OutboundDeliveryService`（统一出站管道，singleton）

`DeliverAsync(OutboundMessage)` 流水线：

1. **目标解析**：`Conversation` → 通道 + DeliveryTarget；支持按 `userId` 解析到其 Direct 对话（工具场景）。
2. **准入强制**：目标不在目录 → 拒绝（目录即授权）；opt-out → 拒绝并附原因；频控超限 → 拒绝。
3. **模式选择**：`ReplyToMsgId` 非空且在被动窗口内 → 被动回复；否则主动消息（受通道主动配额约束，QQ 群/私聊主动消息配额为平台事实）。
4. **投递**：进程内桥（WebUI 组合形态）直调；跨进程经 `GatewayConnectionManager.PushChannelOutbound`（payload v2）。
5. **投递记录**（方案 C 简化吸收）：内存重试队列（host 掉线/瞬态失败）+ 审计日志 `~/.seeing/gateway/outbound-log.jsonl`（追加写）。

### 会话路由绑定

- `SessionData` 废弃 `ChannelId`/`UserId` 首写固定字段，改为结构化 `ChannelRoute`（`ConversationInfo` 快照），由 inbox metadata（`InboxMetadataKeys.ChannelId/UserId` 扩展为结构化 conversation）在首轮写入。
- `GatewayScheduleDispatcher` 改经 `OutboundDeliveryService`（读 `session.ChannelRoute`），删除直组 payload 的旧逻辑。

## Agent 能力（新能力包 `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/`）

只依赖 Abstractions；`AddSeeingModule` 注册 + Activate 挂载：

- `SendMessageTool` — 参数：`target`（userId 或 conversation 标识）、`text`、可选 `mentionUserId`；经 `IChannelMessageSink` 发送；返回结构化结果（成功/失败原因：不在目录/已退订/频控/平台错误）。
- `ListChannelUsersTool` — 按最近活跃分页列出已知用户（"给已知的 gateway 用户推送"的目录入口）。
- `ListConversationsTool` — 列出已知对话（群/私聊）及成员数。
- 无 Gateway 宿主时注册 no-op sink（工具返回"通道投递未配置"），保持工具 schema 稳定。

## 通道桥（QQ + WeCom）收窄为纯翻译器

- **入站**：平台事件 → `GatewayRequest` v2（结构化 Sender/Conversation/ReplyToMsgId）；sessionId 生成保留对话隔离规则但仅为命名（规范格式 `qq:group:{gid}` / `qq:direct:{uid}` / `wecom:…`），不再被反解；`QQSessionTargetParser`、`QQSessionResolver` 反解职责、`WeComSessionResolver` 同类逻辑删除。
- **出站**：payload v2 → 平台 API；被动（msg_id/msg_seq）/主动选择；平台特性降级保留（guild 不支持视频、markdown 校验回退等既有逻辑不动）。
- `/new` 轮转：session 命名可带后缀，路由不依赖命名 → 轮转破坏出站的问题自然消失。
- `/unsubscribe` / `/subscribe` 命令拦截 → 写目录 opt-out 状态（桥拦截命令，经 `IChannelDirectory` Manager 端口落状态；语义与 `/new` 命令拦截一致）。**入站登记不覆盖 opt-out**——退订状态只能由 `/subscribe` 显式清除。

## 数据流

```
被动回复: 用户消息 → 桥翻译 → Submit(登记目录 + DeliverAsync inbox)
          → 桥订阅会话事件 → 桥直接回复(带 msg_id，既有路径不变)
调度推送: Scheduler → OutboundDeliveryService(session.ChannelRoute)
          → channel host → 平台
工具推送: agent 工具 → IChannelMessageSink → OutboundDeliveryService
          (目录解析 + 准入 + 频控) → channel host → 平台
```

## 错误处理与边界

- 平台 4xx（配额超限/目标不存在）：投递记录标记失败；工具收到结构化失败结果，agent 可在当前对话转告用户；审计日志留痕。
- 退订后推送：拒绝 + 原因 `"user_opted_out"`；入站消息重新登记目录条目但**不清除** opt-out，恢复仅经 `/subscribe`。
- 频控默认值：20 条/用户/小时（`GatewayOptions.OutboundRateLimitPerHour`，0 关闭）；超限拒绝 + 原因 `"rate_limited"`。
- Channel host 掉线：`PushChannelOutbound=false` → 重试队列（指数退避，上限 3 次）+ 告警日志；重试仍失败落审计。
- 群共享会话：`session.ChannelRoute` 绑定群对话；调度推送回群（符合"来源对话"语义）；创建者私聊推送由 agent 工具显式指定 target 完成（不做隐式扇出——扇出为后续可选演进）。

## 顺带收敛的架构债（本次一并完成）

| 债务 | 处置 |
|---|---|
| `SessionExecutionQueue`（Gateway 私有副本） | 删除——`DeliverAsync` 为写入+激活，运行时消费循环已保证同会话串行 |
| `GatewayRunTracker` 单订阅覆盖 | 引用计数化（同会话多订阅各持 CTS，spec §4.6/N2） |
| 网关 cancel 用 `StopAsync`（级联子会话+丢弃未读） | 改 `InterruptAsync`（硬停当前轮，继续消费） |
| `GatewaySessionService.ResetAsync` 绕过运行时直改会话 | 先 `runtime.InterruptAsync/StopAsync` 再清理，写回经运行时之后单次 Save |
| 双事件映射器（协议层 `GatewayEventMapper` throw 式 vs `ChatEventMapper` 跳过式，覆盖面漂移、缺 `LoopCancelledEvent`） | 合并为协议层单一 `GatewayEventMapper`（跳过未知 + 补 `LoopCancelledEvent`/`CommandResult`/`Navigate`/`SkillContent`），删除 `ChatEventMapper` |
| `GatewayOrchestratorV2.PersistSessionAsync` 兜底写 | 删除——运行时每轮已落盘（docs 10/13 单一写入者） |

## 测试策略

- `OutboundDeliveryService` 纯单测：目标解析 / 目录即授权 / opt-out / 频控 / 被动-主动选择 / 重试与审计。
- `ChannelDirectoryService`：Submit 登记 → 条目 + 成员关系 + lastSeen；退订/恢复。
- 桥翻译单测：QQ（群/私聊/频道 × 入站解析、出站 target）、WeCom 同矩阵。
- 端到端：模拟 channel host WS 注册 + payload v2 投递 + host 掉线重试。
- 回归：调度推送至群会话 / 私聊会话（旧 bug 场景各一例，含 `/new` 轮转后）。

## 实施顺序（供 writing-plans 展开）

1. 契约层 DTO + 端口（Abstractions/Channels/）
2. 协议 v2（GatewayRequest / channel.outbound payload）
3. 服务器端：ChannelDirectoryService + OutboundDeliveryService + SessionRoute 绑定 + 调度器改造
4. QQ / WeCom 桥翻译器化（删反解）
5. 工具能力包 + Sink 注册（Gateway 模块 Activate）
6. 架构债收敛（上表六项）
7. 测试补齐 + 文档（architecture 17 号 Release Notes）
