# Seeing.Gateway.Console.Demo

最小 Gateway 客户端：从 stdin 读取消息，经 Gateway 调用 Agent，打印流式事件。

用于验证 **Client → Gateway Server → AgentExecutor** 全链路，无需企微或其他 IM。

## 前置条件

1. 启动 Gateway Server（推荐）：

```bash
dotnet run --project samples/Seeing.Gateway.Server
```

或本地开发时启动 WebUI（已联启 Gateway）：

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

验证 Gateway 是否就绪：

```bash
curl http://127.0.0.1:8765/api/gateway/health
```

应返回 `{"status":"healthy",...}`。

## 配置

[`appsettings.json`](appsettings.json)：

```json
{
  "Gateway": {
    "BaseUrl": "http://127.0.0.1:8765",
    "Timeout": "00:30:00"
  }
}
```

WebSocket 模式可在运行时指定 `--transport ws`，或设置：

```json
{
  "Gateway": {
    "BaseUrl": "http://127.0.0.1:8765",
    "Transport": "WebSocket",
    "WebSocketPath": "/api/gateway/ws"
  }
}
```

## 运行

```bash
# HTTP + SSE（默认）
dotnet run --project samples/Seeing.Gateway.Console.Demo

# 指定 sessionId
dotnet run --project samples/Seeing.Gateway.Console.Demo -- my-session-id

# WebSocket 传输
dotnet run --project samples/Seeing.Gateway.Console.Demo -- --transport ws
```

交互：

```
> 你好
（流式打印 assistant 文本增量）
> exit
```

## 输出说明

- `Content` + `delta=true`：直接打印文本增量
- 其他事件：打印 `[Object/Status] {json}`

## 相关文档

- [Gateway 总览](../../docs/gateway/README.md)
- [Gateway Server](../Seeing.Gateway.Server/README.md)
- [Client SDK](../../src/Seeing.Gateway.Client/README.md)
- [Server 集成](../../src/Seeing.Agent.Gateway/README.md)
