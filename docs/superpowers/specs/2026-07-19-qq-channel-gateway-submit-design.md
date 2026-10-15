# QQ Channel + Gateway Submit/Subscribe 迁移设计

**日期:** 2026-07-19  
**状态:** 已审阅；实现计划见 `docs/superpowers/plans/2026-07-19-qq-channel-gateway-submit.md`（不自动提交 git）  
**范围:** App 废弃接口清理、Gateway 协议升级、WeCom 跟进、新增官方 QQ Channel

---

## 背景与目标

### 问题

1. `IChatOrchestrator` 已提供 `SubmitAsync` + `SubscribeEvents` + `Cancel(executionId)`，但 `ExecuteAsync` / `Stop(sessionId)` 仍存在（已标 Obsolete）。WebUI 已切新接口；Gateway Server 仍调用 `ExecuteAsync`。
2. Gateway Client / WeCom 仍使用耦合式 `ChatAsync` + `StopChatAsync(sessionId)`，与 App 模型不一致。
3. 项目缺少官方 QQ 机器人 Channel；需参考 QwenPaw `qq` 与现有 WeCom 架构新增。

### 目标

- **直接删除** App / Gateway / Channel 上的旧耦合执行接口（不保留 Obsolete 适配层）。
- Gateway 对外协议对齐 Submit / Subscribe / Cancel。
- WeCom Bridge 迁移到新 Client API；企微 `aibot_*` 长连接协议不变。
- 新增内置 `Seeing.Gateway.QQ`（官方 Bot API，对齐 QwenPaw `qq` 能力档 C）。
- **不做** OneBot；**不改** 企微 Bot 协议本身。

### 非目标

- OneBot v11 / NapCat 等第三方协议
- 企微 Webhook 短连接模式
- 通用 Channel 抽象大重构（仍按 WeCom 镜像包结构扩展）

---

## 决策摘要

| 决策 | 选择 |
|------|------|
| QQ 协议 | 官方 QQ 机器人（AppID + ClientSecret） |
| QQ 首版能力 | C：尽量对齐 QwenPaw QQ（含频道、富媒体、权限键盘） |
| Gateway 改造 | 方案 1：协议升级为 Submit/Subscribe，旧 API 直接删除 |
| WeCom「废弃」含义 | App/Gateway 耦合接口，非企微 aibot 协议 |
| 兼容策略 | 不标记废弃，直接清理 |

---

## 架构

```
┌─────────────┐     submit / cancel / events      ┌──────────────────┐
│ WeCom Bridge│ ─────────────────────────────────▶│ Gateway Server   │
│ QQ Bridge   │◀─────────────────────────────────│ Orchestrator     │
└─────────────┘   chat.event / execution.complete └────────┬─────────┘
                                                           │
                                              SubmitAsync / SubscribeEvents / Cancel
                                                           │
                                                  ┌────────▼─────────┐
                                                  │ IChatOrchestrator│
                                                  └──────────────────┘

QQ 侧额外:
  QQ 用户 ──WS──▶ QQConnection ──▶ QQChannelBridge
  QQChannelBridge ──HTTP──▶ api.sgroup.qq.com (回复/媒体/interaction ack)
```

三条生命周期（与 WeCom 文档一致，QQ 同样遵守）：

| 层级 | 职责 | 断线行为 |
|------|------|----------|
| Connection | 外部通道连接（企微 WS / QQ WS+Token） | 可重连；不取消 Message Job |
| Message Job | Gateway Submit + 事件消费 + 权限 | 持有 `executionId`；超时/中止调 Cancel |
| Outbound Reply | 通道侧回复（企微 stream / QQ 整段 HTTP） | 与 Job 绑定，不与 TCP epoch 强绑 |

---

## 1. App 层清理

### 删除

- `IChatOrchestrator.ExecuteAsync`
- `IChatOrchestrator.Stop`
- `ChatOrchestrator` 中对应实现
- 全仓库调用点改为 `SubmitAsync` + `SubscribeEvents` + `Cancel`

### 保留（唯一执行入口）

- `SubmitAsync(sessionId, input, options)` → `ExecutionSubmitResult`（含 `executionId`、Queued 等）
- `SubscribeEvents(sessionId, ct)` → `IAsyncEnumerable<IMessageEvent>`
- `Cancel(executionId)`
- `GetOverview` / `GetExecution` / `GetBufferedEvents`

### 订阅完成判定

消费者必须匹配 **本 job 的 `executionId`**（例如 `ExecutionCompleteEvent.ExecutionId`），避免同 session 排队时误结束。

---

## 2. Gateway 协议与 Client

### Server

`GatewayOrchestratorV2`：

1. `SubmitAsync` 提交执行
2. 若失败，立即返回错误事件/帧
3. `SubscribeEvents(sessionId)` 映射为 `GatewayEvent` 写出
4. 仅在收到匹配 `executionId` 的完成事件后结束该 job 的推送（连接级订阅可继续）
5. 取消：`Cancel(executionId)`，不再按 session 盲停

### HTTP（替换旧路径）

| 新 | 说明 |
|----|------|
| `POST /api/gateway/submit` | body=`GatewayRequest`，返回 `executionId` 等 |
| `POST /api/gateway/cancel` | body 含 `executionId` |
| `GET /api/gateway/events?sessionId=`（SSE） | 订阅该 session 事件流；客户端先 `submit` 再按 `executionId` 过滤完成 |

**删除：** `POST /api/gateway/chat`、`POST /api/gateway/chat/stop`

权限、session reset、health 等端点保留。

### WebSocket 帧

| 方向 | Type | 说明 |
|------|------|------|
| C→S | `submit` | payload=`GatewayRequest`；ack 含 `executionId` |
| C→S | `cancel` | payload 含 `executionId` |
| S→C | `chat.event` | 流式事件；payload 带 `sessionId`，并携带/可关联 `executionId` |
| S→C | `execution.complete` | 替代或演进自 `chat.complete`，必须含 `executionId` + `sessionId` |
| C→S / S→C | permission* | 保持现有权限帧，与 submit 解耦 |

**删除：** 旧 `chat`（耦合一次请求一条流）、`stop`（按 sessionId）帧类型。

`GatewayConnectedPayload.Capabilities` 更新为含 `submit`、`cancel`、`permission` 等。

### Client 契约

`IGatewayClient` / `IGatewayConnection`：

- `SubmitAsync(GatewayRequest)` → 含 `executionId` 的结果
- 事件消费：`ReceiveAsync` 过滤，或显式 `SubscribeAsync(sessionId)`（实现可选，语义一致即可）
- `CancelAsync(executionId)`
- 保留：`RespondPermissionAsync`、`ResetSessionAsync`、`GetPendingPermissionsAsync`（HTTP）

**删除：** `ChatAsync`、`StopChatAsync(sessionId)`、`WebSocketGatewayClientAdapter`（旧耦合适配器整类删除）。Channel 直接使用 `IGatewayConnection.SubmitAsync` + `ReceiveAsync`（按 `executionId` 过滤）+ `CancelAsync`。

---

## 3. WeCom 迁移

### 改

- `WeComChannelBridge`：Message Job 使用 `SubmitAsync`；循环消费事件直至本 `executionId` 完成或失败
- 超时 / Bridge 停止 / 用户中止：`Cancel(executionId)`
- 测试与 `README` 中 `ChatAsync` 表述全部更新

### 不变

- `aibot_subscribe` / `aibot_msg_callback` / `aibot_event_callback`
- `reply_stream`、template_card、Connection 状态机、Outbound Governor
- Session 映射、`/new` `/clear`、媒体解密拉取

### 风险处理

- **Queued：** Bridge 展示「处理中」占位，直到本 `executionId` 事件开始/结束
- **流式中权限：** 继续现有策略（stream 打开时不发 template_card 抢被动槽，改为 stream 内提示或完成后再卡）

---

## 4. QQ Channel（`Seeing.Gateway.QQ`）

### 定位

- 内置插件：`ChannelId = "qq"`，`builtin:qq`
- 镜像 WeCom 包结构与 DI/注册方式
- Gateway 仅走新 Submit/Subscribe/Cancel

### 配置（`QQOptions`）

| 字段 | 说明 |
|------|------|
| `Enabled` | 是否启用 |
| `AppId` / `ClientSecret` | 官方机器人凭证 |
| `ApiBase` | 默认 `https://api.sgroup.qq.com` |
| `BotPrefix` | 可选回复前缀 |
| `MarkdownEnabled` | 默认 true |
| `ShareSessionInGroup` | 群会话共享 |
| `SessionIdleTimeoutMinutes` | 对齐 WeCom |
| `MaxReconnectAttempts` | 重连上限 |
| `AckMessage` | 可选收到消息即时提示 |
| `MediaCacheDirectory` / `MaxMediaBytes` | 媒体缓存 |
| 权限相关 | 对齐 WeCom 的 risk/kind 策略；出站用 keyboard |

### 消息类型

入站（WS intents，参考 QwenPaw）：

- `C2C_MESSAGE_CREATE`
- `GROUP_AT_MESSAGE_CREATE`
- `AT_MESSAGE_CREATE` / `DIRECT_MESSAGE_CREATE`（频道）
- `INTERACTION_CREATE`（权限按钮）

出站（HTTP）：

- C2C / Group / Channel 发消息（markdown + keyboard）
- 富媒体上传与发送
- `PUT /interactions/{id}` ack

### Session 映射

对齐 WeCom 风格（文件系统安全字符，不含 `:`）；`ShareSessionInGroup` 默认 `true`。

| 场景 | sessionId |
|------|-----------|
| C2C | `qq_{user_openid}` |
| 群（共享） | `qq_group_{group_openid}` |
| 群（不共享） | `qq_group_{group_openid}_{member_openid}` |
| 频道 AT / 频道私信 | `qq_channel_{channel_id}` |

出站回帖所需的 `msg_id` / `group_openid` / `channel_id` 等放在 Bridge 侧 conversation 元数据中，不编码进 sessionId。

### 流式策略

QQ 无企微式 `reply_stream`：

- Gateway 增量在 Bridge 内合并/节流
- **终态发送一条**完整回复（可选先发 ack/processing 文本）
- 不假装流式气泡协议

### 权限

- 出站：markdown + keyboard（approve/deny），action data 编码 `permissionId` / session 上下文
- 入站：`INTERACTION_CREATE` → ack → `RespondPermissionAsync`
- 去重：同一 permission/request 防重复点击

### 连接

- `getAppAccessToken` 缓存（提前刷新）
- WS Hello / Identify / Resume / Heartbeat
- 可恢复断线退避；快速断线熔断策略参考 QwenPaw

### 项目结构（示意）

```
src/Seeing.Gateway.QQ/
  QQChannelPlugin.cs
  QQOptions.cs
  QQChannelBridge.cs
  Connection/          # WS、HTTP API、Token、重连
  QQMessageParser.cs
  QQSessionResolver.cs
  QQCommandInterceptor.cs
  QQPermission*.cs
  QQMediaFetcher.cs
  Extensions/QQServiceCollectionExtensions.cs
  README.md

tests/Seeing.Gateway.QQ.Tests/
samples/...  # ChannelHost 引用 QQ
```

`GatewayChannelRegistry.RegisterBuiltinPlugins` 注册 `QQChannelPlugin`。

---

## 5. WebUI 配置与管理

初版设计未单独写 WebUI；现有 **Gateway 客户端** 页已是注册表 + Schema 驱动，QQ 应复用该路径，**不新增专用 QQ 设置页**。

### 现有能力（直接复用）

| 能力 | 位置 | QQ 如何受益 |
|------|------|-------------|
| 客户端列表 / 内置筛选 | `GatewayClientsPage` | Registry 出现 `qq` 后自动展示 |
| 启用开关、启停、重启 | `GatewayClientSupervisor` | 启动 ChannelHost，`--plugin` 指向 QQ 程序集，`--config` 指向运行时 JSON |
| 配置抽屉 | `GatewayClientDrawer` + `SchemaDrivenConfigForm` | 由 `QQOptions` 的 `[Display]` / `GetConfigSchema()` 生成表单 |
| 配置落盘 | `GatewayClientConfigService` | `.seeing/gateway-clients/qq.json`；`seeing.json` 仅 `GatewayClients.Channels.qq.Enabled` + `PluginSpec` |

### 必须接线的工程点

1. `GatewayChannelRegistry` 增加 `builtin:qq` / `QQChannelPlugin`（与 wecom 并列）
2. `Seeing.Agent.Gateway`、`Seeing.Gateway.ChannelHost` 引用 `Seeing.Gateway.QQ`（保证内置发现与 Host 能加载 DLL）
3. `QQOptions` 字段补齐 `[Display]` / `[Required]`，与 WeCom 一致，保证 WebUI 表单可用
4. 解决方案 / slnx 纳入 QQ 项目；WebUI 构建仍 Copy ChannelHost（随 Host 引用自动带上 QQ DLL）

### 不做

- 不为 QQ 单独做 Blazor 自定义 `ConfigFormComponentType`（除非 Schema 表单不够用再补）
- 不在 Session 页做 QQ 专属 UI（会话仍按 `qq_*` sessionId 在现有会话列表可见）

### 验收（WebUI）

- [ ] 「设置 → Gateway 客户端」可见内置「QQ」卡片
- [ ] 可填写 AppId/ClientSecret 等并保存到 `.seeing/gateway-clients/qq.json`
- [ ] Enabled 后可启动/停止进程；日志可见 ChannelHost `[QQ]`

---

## 6. 实施顺序

1. App：删除 `ExecuteAsync` / `Stop`，修全仓库引用  
2. Gateway Server + Protocol + Client：submit/cancel/events；删除旧 chat/stop  
3. WeCom：Bridge + 测试 + README  
4. QQ：新包 → 文本路径 → 媒体/权限/频道 → **Registry/Host/WebUI 接线**  
5. 回归：`dotnet test`；WebUI Gateway 客户端页验证 QQ 配置与启停  

---

## 7. 测试与验收

### 测试

| 层级 | 内容 |
|------|------|
| App | 无 Execute/Stop；Submit+Subscribe 完成匹配 executionId |
| Gateway | submit ack、cancel、事件过滤、权限帧回归 |
| WeCom | 现有用例改新 Client；重连/流式/权限/session 命令 |
| QQ | 事件解析、session key、出站 payload、interaction ack；假 HTTP/WS |
| WebUI | Registry 含 qq；配置读写路径；启停不回归（可手工） |

### 验收标准

- [ ] 仓库无 `IChatOrchestrator.ExecuteAsync` / `Stop` 定义与调用
- [ ] Gateway/WeCom/Client 无 `ChatAsync` / `StopChatAsync(sessionId)`
- [ ] WeCom 在真机或现有测试下行为不回退
- [ ] QQ：配置 AppId/Secret 后可 C2C/群收发；权限键盘可批准/拒绝；频道与富媒体按 C 档覆盖
- [ ] WebUI Gateway 客户端可配置/启停 QQ（见 §5）
- [ ] 相关 `dotnet test` 通过

---

## 8. 风险与缓解

| 风险 | 缓解 |
|------|------|
| 同 session 多 execution 事件交错 | 严格按 `executionId` 过滤 |
| QQ 限流 / 快速断线 | 退避、熔断、出站节流（参考 QwenPaw） |
| Gateway 协议破坏性变更 | 本轮明确不做旧 API 兼容；ChannelHost/文档同步升级 |
| QQ 官方 API 细节差异 | 以 QwenPaw `qq/channel.py` + 官方文档为准，合同测试锁 payload |
| WebUI 看不到 QQ | 确认 Gateway 工程引用 + Registry 注册 + ChannelHost 输出含 QQ DLL |

---

## 参考

- App：`IChatOrchestrator`、`ExecutionJobService`、WebUI `Session.razor` Submit 路径
- Gateway：`GatewayOrchestratorV2`、`Seeing.Gateway.Client`、`Seeing.Gateway.WeCom`
- WebUI：`GatewayClientsPage`、`GatewayClientConfigService`、`GatewayClientSupervisor`、`GatewayChannelRegistry`
- QwenPaw：`src/qwenpaw/app/channels/qq/channel.py` 及 `cards/`
- 企微长连接：现行 `aibot_*`（本设计不修改）
