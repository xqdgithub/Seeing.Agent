# Seeing.Gateway.WeCom

**企业微信 AI Bot** Channel Bridge：通过企微官方 WebSocket 长连接接收消息，经 Gateway WebSocket 调用 Agent，流式回复到企微。

> 企微无官方 C# SDK，本包自实现薄协议层，参考 [wecom-aibot-python-sdk](https://github.com/WecomTeam/wecom-aibot-python-sdk) 与[智能机器人长连接文档](https://developer.work.weixin.qq.com/document/path/101463)。

## 架构

```
企微用户 ──▶ wss://openws.work.weixin.qq.com
              │
              ▼
       WeComConnectionManager (状态机 / epoch / 重连)
              │
              ▼
       WeComAibotWsClient (façade)
              │
              ▼
       WeComChannelBridge ──▶ WebSocketGatewayClient ──▶ Gateway Server
              │                        ▲
              │                        │ Message Job（独立取消域）
              ▼                        │
         WeComStreamState ──▶ WeComOutboundChannel + Governor
              │
              ▼
         reply_stream (流式气泡)
```

### 三条独立生命周期

| 层级 | 职责 | 断线行为 |
|------|------|----------|
| **Connection** | WS 传输、subscribe、心跳、epoch、重连 | transient 断线：暂停出站，自动重连 |
| **Message Job** | Gateway `ChatAsync`、权限、超时 | **不因** transient 断线取消 |
| **Stream Reply** | 单条 `stream_id` 协议合规 | 断线缓存 pending；重连后同 `stream_id` 恢复推送 |

- 连接可重建；**回复义务**绑定用户消息，不绑定 TCP epoch
- transient 断线（Stopping / Disconnected / Backoff）：`PauseAll` + 重连后 `FlushAll`
- fatal 断线（Superseded / Failed / Bridge 停止）：`AbortAll`

## 安装

```bash
dotnet add package Seeing.Gateway.WeCom
```

## DI 注册

```csharp
using Seeing.Gateway.WeCom.Extensions;

services.Configure<WeComOptions>(configuration.GetSection("WeCom"));
services.AddSeeingWeComChannel(configureGateway: g =>
{
    g.BaseUrl = "http://127.0.0.1:8765";
});
services.AddHostedService<WeComBridgeHostedService>(); // 见 Demo
```

## 配置项

| 配置键 | 默认值 | 说明 |
|--------|--------|------|
| `Enabled` | `false` | 是否启用 |
| `BotId` | — | 企微后台机器人 ID |
| `Secret` | — | 机器人 Secret |
| `WsUrl` | `wss://openws.work.weixin.qq.com` | 长连接地址 |
| `ShareSessionInGroup` | `true` | 群聊共享 session |
| `SessionIdleTimeoutMinutes` | `30` | 空闲超时（分钟）后自动轮换 sessionId；`0` 禁用 |
| `ResetOnEnterChatWhenIdle` | `true` | 用户重新打开聊天且已超时时自动新 session |
| `SessionStateFile` | `.seeing/gateway-clients/wecom.sessions.json` | conversationKey 映射持久化 |
| `AppendCommandHintsToWelcome` | `true` | 欢迎语附加 `/new`、`/clear` 提示 |
| `MaxReconnectAttempts` | `-1` | 最大重连次数（-1 无限） |
| `StreamingEnabled` | `true` | 流式 reply_stream |
| `BotPrefix` | — | 回复文本前缀 |
| `WelcomeText` | 默认欢迎语 | enter_chat 5s 内回复 |
| `AutoApproveLowRisk` | `true` | 低风险权限自动批准 |
| `PromptRiskLevels` | `high,critical` | 需弹卡片的风险等级 |
| `PromptPermissionKinds` | `shell,file,mcp_tool` | 需弹卡片的权限类型 |
| `MediaCacheDirectory` | `%TEMP%/seeing-wecom-media` | 入站媒体缓存 |
| `MaxMediaBytes` | `10485760` | 单文件大小上限 |
| `PermissionCardTtlSeconds` | `600` | 权限卡片 task_id 映射 TTL |
| `HeartbeatIntervalSeconds` | `30` | 心跳间隔（0 表示默认 30 秒） |
| `DeltaThrottleMilliseconds` | `150` | 流式增量节流（0 表示默认 150ms，防限流） |
| `ProcessingRefreshSeconds` | `20` | Thinking 占位刷新间隔 |
| `ProcessingMaxDurationSeconds` | `180` | 单条消息 Job 最长等待 Agent 回复（秒） |

### appsettings.json 示例

```json
{
  "WeCom": {
    "Enabled": true,
    "BotId": "your-bot-id",
    "Secret": "your-bot-secret",
    "ShareSessionInGroup": true,
    "StreamingEnabled": true,
    "WelcomeText": "你好！我是 Seeing Agent，有什么可以帮你的？",
    "AutoApproveLowRisk": true
  },
  "Gateway": {
    "BaseUrl": "http://127.0.0.1:8765",
    "Transport": "WebSocket"
  }
}
```

默认 Agent 由 Gateway Server 的 `.seeing/seeing.json` 中 `DefaultAgent` 决定（WeCom 请求不传 `AgentId`）。

## Session 映射

| 场景 | sessionId |
|------|-----------|
| 单聊 | `wecom_{userid}` |
| 群聊（ShareSessionInGroup=true） | `wecom_group_{chatid}` |

与 QwenPaw 基础映射一致，可在 WebUI 中用相同 ID 查看会话历史。

### 会话生命周期

| 触发条件 | 行为 |
|----------|------|
| 距上次消息超过 `SessionIdleTimeoutMinutes` | 自动轮换为新 `sessionId`（旧会话文件保留） |
| 用户发送 `/new` | 立即轮换为新 `sessionId` |
| 用户发送 `/clear` | 保持当前 `sessionId`，调用 Gateway 清空消息历史 |
| `enter_chat` 且已超时 | 先轮换 session，再发送欢迎语 |

群聊中 `@机器人 /clear` 会自动剥离 @mention 后识别命令。普通群聊消息也会剥离 leading `@机器人`，避免污染 Agent 上下文。

### 引用消息

用户引用某条消息再提问时，企微 `aibot_msg_callback` 会同时下发 `text`（用户提问）与 `quote`（被引用内容）。Bridge 解析后写入 `GatewayRequest.Quote`，Gateway Server 合成为 XML 边界 user turn：

```xml
<quoted_message type="text" source="wecom">
被引用的正文
</quoted_message>

<user_message>
数据来源是什么
</user_message>
```

| quote.msgtype | Bridge 行为 |
|---------------|-------------|
| text | 转发引用文本 |
| voice | 优先语音转写文本；否则下载解密为 audio part |
| image / file / video | 下载解密为对应 GatewayContentPart |
| mixed | 遍历 `msg_item` 递归解析 |

引用解析失败时降级为仅转发用户提问，不阻断主消息。

与 QwenPaw 的差异：QwenPaw 无空闲自动轮换；`/new` 在同 session 内清内存，Seeing.Agent 的 `/new` 会生成新 session 文件。

## 流式行为

企微协议约束：**每条用户消息仅一条 `stream_id`**，且只有最终 `Complete` 可发送 `finish=true`。

1. `WeComStreamState.BeginAsync` → 分配 `stream_id`，发送 `🤔 Thinking...`（`finish=false`）
2. Gateway `Content`+Delta → `PublishAsync` 在同一 stream 上更新正文（节流）
3. Gateway `LoopComplete` → `CompleteAsync` 发送 `finish=true`
4. 企微 `msgtype=stream` 刷新回调 → 使用**刷新帧的 req_id** 回传当前内容（`finish=false`）
5. Keepalive 仅刷新 Thinking（`finish=false`），**不会**提前结束 stream
6. `ProcessingMaxDurationSeconds` 由 Bridge 用于取消 Gateway 请求，而非关闭 stream
7. **长连接模式**下流式刷新由 Bridge **主动推送**（`WeComStreamState` ProcessingKeepalive）；`msgtype=stream` 刷新回调路径仅适用于 Webhook 模式

Channel Bridge 通过 `GatewayAssistantReplyCollector` 按 Gateway 完成信号契约消费事件流。

### 长任务与重连

ACP / 长工具链任务可能持续数分钟，期间 WeCom 连接可能 transient 断开并重连（epoch 递增）：

1. **Message Job** 使用 Bridge 级 token + `ProcessingMaxDurationSeconds`，**不**绑定连接 `sessionCts`
2. transient 断线时 **不会** `AbortAll` 进行中的 stream；仅暂停 keepalive
3. 连接恢复（`Active`）后，所有 active stream **Flush** 最新内容（同一 `stream_id`）
4. `CompleteAsync` / `FailAsync` 在连接不可用时等待恢复（最长 60s），确保 `finish=true` 送达
5. **Outbound Governor** 限制全连接出站 ≤ 25 条/分钟，避免触发企微约 30 条/分钟限流

## 企微 WebSocket 协议（本包实现子集）

| cmd | 方向 | 说明 |
|-----|------|------|
| `aibot_subscribe` | C→S | 认证（bot_id + secret） |
| `ping` / `pong` | 双向 | 心跳 |
| `aibot_msg_callback` | S→C | 入站消息 |
| `aibot_event_callback` | S→C | 入站事件（enter_chat / 模板卡片点击） |
| `aibot_respond_msg` | C→S | 回复（含 stream / template_card） |
| `aibot_respond_welcome_msg` | C→S | enter_chat 欢迎语 |
| `aibot_respond_update_msg` | C→S | 更新模板卡片 |

流式回复 body：

```json
{
  "msgtype": "stream",
  "stream": {
    "id": "stream_xxx",
    "content": "回复内容",
    "finish": false
  }
}
```

## 当前范围（MVP）

| 能力 | 状态 |
|------|------|
| 文本消息入站/流式回复 | ✅ |
| 图片/语音/文件/视频入站 | ✅（解密下载 → GatewayContentPart） |
| enter_chat 欢迎语 | ✅ |
| 混合权限（低风险自动批准 + 高风险模板卡片） | ✅ |
| 消息去重（msgid） | ✅ |
| 断线重连（指数退避） | ✅ |
| 出站图片/文件/语音 | ❌ 待实现（需 media upload） |
| `aibot_send_msg` 主动推送 | ❌ 待实现 |

## 限制

- 同一机器人仅允许 **一个** 有效长连接；不可多实例部署
- 企微频率限制约 30 条/分钟、1000 条/小时；Bridge 通过 **Outbound Governor** + delta 节流治理
- 串行化由 **Gateway Server** `SessionExecutionQueue` 负责；Bridge 无全局锁，不在 WS 回调线程阻塞
- Gateway Server 建议配置 `PermissionMode: interactive`，由 Bridge 混合策略处理权限

## 示例

见 [samples/Seeing.Gateway.WeCom.Demo](../../samples/Seeing.Gateway.WeCom.Demo/README.md)。

## 测试

```bash
dotnet test tests/Seeing.Gateway.WeCom.Tests
```
