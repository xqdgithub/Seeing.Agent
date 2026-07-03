# Seeing.Gateway.WeCom.Demo

独立控制台进程：连接 **企业微信 AI Bot WebSocket**，经 **Gateway WebSocket** 调用 Seeing.Agent，流式回复到企微。

```
企微用户 → WeCom WS → WeComChannelBridge → Gateway WS → Agent → 流式 reply_stream → 企微用户
```

## 前置条件

### 1. 企微后台

1. 创建智能机器人，启用 **长连接** 模式
2. 记录 `BotId` 与 `Secret`
3. 参考：[智能机器人长连接](https://developer.work.weixin.qq.com/document/path/101463)

### 2. Gateway Server

终端 1 — 启动独立 Gateway Server（推荐）：

```bash
dotnet run --project samples/Seeing.Gateway.Server
```

确保 `PermissionMode` 为 `interactive`（Bridge 负责混合权限：低风险自动批准，高风险弹模板卡片）。

也可使用 `~/.seeing/wecom.json` 统一配置 Bot 与 Gateway 地址（Demo `Program.cs` 会自动加载）。

## 配置

编辑 [`appsettings.json`](appsettings.json) 或 `~/.seeing/wecom.json`：

```json
{
  "WeCom": {
    "Enabled": true,
    "BotId": "替换为你的-bot-id",
    "Secret": "替换为你的-secret",
    "WsUrl": "wss://openws.work.weixin.qq.com",
    "ShareSessionInGroup": true,
    "StreamingEnabled": true,
    "WelcomeText": "你好！我是 Seeing Agent，有什么可以帮你的？",
    "AutoApproveLowRisk": true
  },
  "Gateway": {
    "BaseUrl": "http://127.0.0.1:8765",
    "WebSocketPath": "/api/gateway/ws",
    "Transport": "WebSocket"
  }
}
```

开发环境可将 `appsettings.Development.json` 中 `WeCom.Enabled` 设为 `false` 以免误连。

也可通过环境变量覆盖（ASP.NET Core 标准）：

```bash
set WeCom__BotId=your-bot-id
set WeCom__Secret=your-secret
dotnet run --project samples/Seeing.Gateway.WeCom.Demo
```

## 运行

终端 2：

```bash
dotnet run --project samples/Seeing.Gateway.WeCom.Demo
```

日志示例：

```
Seeing Gateway WeCom Demo
WeCom 订阅成功
WeCom Channel Bridge 已启动
WeCom 收到消息: UserId=xxx, SessionId=wecom_xxx, Parts=1
```

在企微中向机器人发送文本，应看到 Thinking 占位后流式回复。首次进入单聊会收到欢迎语。

## 联调检查清单

| 检查项 | 预期 |
|--------|------|
| Gateway 健康 | `curl http://127.0.0.1:8765/api/gateway/health` 返回 healthy |
| Gateway 已启动 | Gateway.Server 日志含 Gateway 监听信息 |
| 企微连接 | Demo 日志含 `WeCom 订阅成功` |
| 仅一个 Bridge 实例 | 同一 Bot 不可多进程同时连企微 WS |
| 权限 | Gateway `PermissionMode: interactive` + Bridge 混合策略 |

## 故障排查

| 现象 | 可能原因 |
|------|----------|
| 订阅失败 errcode≠0 | BotId/Secret 错误或机器人未启用长连接 |
| Gateway 连接失败 | Gateway.Server 未启动或端口不是 8765 |
| 无流式回复 | `StreamingEnabled=false` 或 Agent/Provider 未配置 |
| 回复中断 | 企微流超时；检查 keepalive 配置（默认 20s/180s） |
| 重复回复 | msgid 去重失效；检查是否多实例运行 |
| 无欢迎语 | enter_chat 事件需在 5s 内回复；检查 Bridge 日志 |

## 相关文档

- [Gateway 总览](../../docs/gateway/README.md)
- [WeCom Bridge 包](../../src/Seeing.Gateway.WeCom/README.md)
- [Gateway Server](../Seeing.Gateway.Server/README.md)
