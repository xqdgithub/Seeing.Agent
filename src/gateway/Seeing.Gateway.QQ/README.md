# Seeing.Gateway.QQ

QQ 官方机器人 Channel Bridge：WebSocket 收事件，HTTP 发回复，经 Gateway Submit/Subscribe/Cancel 调用 Agent。

## 配置

写入 `.seeing/gateway-clients/qq.json`（WebUI「Gateway 客户端」可编辑）：

```json
{
  "QQ": {
    "Enabled": true,
    "AppId": "...",
    "ClientSecret": "..."
  },
  "Gateway": {
    "BaseUrl": "http://127.0.0.1:8765",
    "Transport": "WebSocket"
  }
}
```

## Session

| 场景 | sessionId（`:` 会换成文件系统安全的 `_`） |
|------|-----------|
| C2C / 私信 | `qq:direct:{openid}` |
| 群共享 | `qq:group:{group_openid}` |
| 频道 | `qq:guild:{channel_id}` |

## 与 WeCom 差异

QQ 无 `reply_stream`：Bridge 合并增量后**终态发一条** HTTP 消息。
