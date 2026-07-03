# Seeing.Gateway.Client

Gateway **客户端 SDK**：通过 HTTP+SSE 或 WebSocket 与 Gateway Server 通信。

## 安装

```bash
dotnet add package Seeing.Gateway.Client
```

## DI 注册

```csharp
using Seeing.Gateway.Client.Extensions;

// 从配置节 "Gateway" 注册
services.AddSeeingGatewayClient(configuration);

// 或代码配置
services.AddSeeingGatewayClient(options =>
{
    options.BaseUrl = "http://127.0.0.1:8765";
    options.Transport = GatewayClientTransport.WebSocket;
    options.WebSocketPath = "/api/gateway/ws";
    options.Timeout = TimeSpan.FromMinutes(30);
});
```

解析 `IGatewayClient` 即可使用；传输方式由 `GatewayClientOptions.Transport` 决定。

## 配置项

| 配置键 | 默认值 | 说明 |
|--------|--------|------|
| `BaseUrl` | `http://localhost:5000` | Gateway 根地址 |
| `Timeout` | 30 分钟 | HTTP/SSE 长连接超时 |
| `ApiKey` | — | 可选，通过 `X-Api-Key` 头发送 |
| `Transport` | `HttpSse` | `HttpSse` 或 `WebSocket` |
| `WebSocketPath` | `/api/gateway/ws` | WS 路径 |

### appsettings.json 示例

```json
{
  "Gateway": {
    "BaseUrl": "http://127.0.0.1:8765",
    "Transport": "WebSocket",
    "WebSocketPath": "/api/gateway/ws",
    "Timeout": "00:30:00"
  }
}
```

## 使用示例

### HTTP + SSE

```csharp
var client = serviceProvider.GetRequiredService<IGatewayClient>();

var request = new GatewayRequest
{
    SessionId = "demo-session",
    Input = [new GatewayTextContentPart("你好")],
    Stream = true
};

await foreach (var evt in client.ChatAsync(request))
{
    if (evt.Object == GatewayEventObject.Content && evt.Data?.Delta == true)
        Console.Write(evt.Data.Text);
}
```

### WebSocket（底层连接）

```csharp
var wsClient = serviceProvider.GetRequiredService<WebSocketGatewayClient>();
await wsClient.ConnectAsync();

var requestId = await wsClient.SendChatAsync(request);
await foreach (var inbound in wsClient.ReceiveAsync())
{
    if (inbound.Type == GatewayWsFrameType.ChatEvent)
        Console.WriteLine(inbound.Event?.Data?.Text);
    if (inbound.Type == GatewayWsFrameType.ChatComplete && inbound.Id == requestId)
        break;
}
```

`WebSocketGatewayClientAdapter` 将 WS 连接包装为 `IGatewayClient.ChatAsync`，供简单场景使用。

## 端点（Server 提供）

| 方法 | 路径 | Client 用法 |
|------|------|-------------|
| POST | `/api/gateway/chat` | `HttpGatewayClient.ChatAsync`（SSE） |
| WS | `/api/gateway/ws` | `WebSocketGatewayClient` |
| POST | `/api/gateway/chat/stop?sessionId=` | `StopChatAsync` |
| POST | `/api/gateway/sessions/{sessionId}/reset` | `ResetSessionAsync` |
| GET | `/api/gateway/permissions/pending?sessionId=` | `GetPendingPermissionsAsync` |
| POST | `/api/gateway/permissions/{id}/respond` | `RespondPermissionAsync` |
| GET | `/api/gateway/health` | 健康检查 |

## 示例项目

```bash
dotnet run --project samples/Seeing.Gateway.Console.Demo
dotnet run --project samples/Seeing.Gateway.Console.Demo -- --transport ws
```

## 测试

```bash
dotnet test tests/Seeing.Gateway.Client.Tests
```
