# Seeing.Gateway.WeCom

**企业微信 AI Bot** Channel Bridge：通过企微官方 WebSocket 长连接接收消息，经 Gateway WebSocket 调用 Agent，流式回复到企微。

> 企微无官方 C# SDK，本包自实现薄协议层，参考 [wecom-aibot-python-sdk](https://github.com/WecomTeam/wecom-aibot-python-sdk) 与[智能机器人长连接文档](https://developer.work.weixin.qq.com/document/path/101463)。

## 架构

```
企微用户 ──▶ wss://openws.work.weixin.qq.com
              │
              ▼
       WeComAibotWsClient
              │
              ▼
       WeComChannelBridge ──▶ WebSocketGatewayClient ──▶ Gateway Server
              │
              ▼
         reply_stream (流式气泡)
```

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
| `HeartbeatIntervalSeconds` | `30` | 心跳间隔 |
| `DeltaThrottleMilliseconds` | `150` | 流式增量节流（防限流） |
| `ProcessingRefreshSeconds` | `20` | Thinking 占位刷新间隔 |
| `ProcessingMaxDurationSeconds` | `180` | Thinking 占位最长时长 |

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

群聊中 `@机器人 /clear` 会自动剥离 @mention 后识别命令。

与 QwenPaw 的差异：QwenPaw 无空闲自动轮换；`/new` 在同 session 内清内存，Seeing.Agent 的 `/new` 会生成新 session 文件。

## 流式行为

1. 收到文本消息 → 发送 `🤔 Thinking...` 占位流（`finish=false`）
2. Agent 有内容增量 → `reply_stream` 覆盖气泡（150ms 节流）
3. `LoopComplete` → `reply_stream(finish=true)` 结束
4. Keepalive：每 20s 刷新占位，180s 强制 `finish=true` 防止企微丢流

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
- 企微频率限制约 30 条/分钟、1000 条/小时；Bridge 已做 delta 节流
- 串行化由 **Gateway Server** `SessionExecutionQueue` 负责；Bridge 无全局锁，不在 WS 回调线程阻塞
- Gateway Server 建议配置 `PermissionMode: interactive`，由 Bridge 混合策略处理权限

## 示例

见 [samples/Seeing.Gateway.WeCom.Demo](../../samples/Seeing.Gateway.WeCom.Demo/README.md)。

## 测试

```bash
dotnet test tests/Seeing.Gateway.WeCom.Tests
```
