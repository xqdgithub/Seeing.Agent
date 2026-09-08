# Seeing.Gateway.Client

Gateway **客户端 SDK**：通过 HTTP+SSE 或 WebSocket 与 Gateway Server 通信（Submit / Subscribe / Cancel）。

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

- `HttpSse` → `HttpGatewayClient`
- `WebSocket` → `WebSocketGatewayClientFacade`（包装 `WebSocketGatewayClient`）

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

### HTTP：Submit + SSE 订阅

```csharp
var client = serviceProvider.GetRequiredService<IGatewayClient>();

var request = new GatewayRequest
{
    SessionId = "demo-session",
    Input = [new GatewayTextContentPart("你好")],
    Stream = true
};

var submit = await client.SubmitAsync(request);
if (!submit.Success || string.IsNullOrEmpty(submit.ExecutionId))
    throw new InvalidOperationException(submit.Error ?? "Submit failed");

await foreach (var evt in client.SubscribeAsync(request.SessionId, submit.ExecutionId))
{
    if (evt.Object == GatewayEventObject.Content && evt.Data?.Delta == true)
        Console.Write(evt.Data.Text);
}
```

### WebSocket（底层连接）

```csharp
var wsClient = serviceProvider.GetRequiredService<WebSocketGatewayClient>();
await wsClient.ConnectAsync();

var submit = await wsClient.SubmitAsync(request);
// SubmitAck 由客户端内部等待；随后 ReceiveAsync 消费 chat.event / execution.complete

await foreach (var inbound in wsClient.ReceiveAsync())
{
    if (inbound.Type == GatewayWsFrameType.ChatEvent)
        Console.WriteLine(inbound.Event?.Data?.Text);
    if (inbound.Type == GatewayWsFrameType.ExecutionComplete
        && inbound.ExecutionComplete?.ExecutionId == submit.ExecutionId)
        break;
}

// 取消：
await wsClient.CancelAsync(submit.ExecutionId!);
```

`WebSocketGatewayClientFacade` 将 WS 连接包装为 `IGatewayClient`（Submit + Subscribe + Cancel）。

## 端点（Server 提供）

| 方法 | 路径 | Client 用法 |
|------|------|-------------|
| POST | `/api/gateway/submit` | `SubmitAsync` |
| GET | `/api/gateway/events?sessionId=&executionId=` | `SubscribeAsync`（SSE） |
| POST | `/api/gateway/cancel` | `CancelAsync`（body: `{ "executionId" }`） |
| WS | `/api/gateway/ws` | `WebSocketGatewayClient` |
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
