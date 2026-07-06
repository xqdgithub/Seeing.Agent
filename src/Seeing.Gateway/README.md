# Seeing.Gateway

Gateway **协议层**：DTO、客户端/Bridge 接口、事件映射。无 ASP.NET 依赖，可被 Server 插件与 Client SDK 共用。

## 安装

```bash
dotnet add package Seeing.Gateway
```

## 核心模型

### GatewayRequest（入站）

```csharp
public record GatewayRequest
{
    public required string SessionId { get; init; }
    public string? UserId { get; init; }
    public string? ChannelId { get; init; }
    public string? AgentId { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<GatewayContentPart>? Input { get; init; }
    public GatewayQuoteContext? Quote { get; init; }
    public bool Stream { get; init; } = true;
    public Dictionary<string, object?>? Metadata { get; init; }
}
```

`GatewayContentPart` 支持 `text` / `image` / `file` / `audio`（JSON discriminated union）。

`GatewayQuoteContext` 承载用户引用的消息快照（与 `Input` 并列，Channel Bridge 填充，Gateway Server 合成 XML 边界 user turn）：

```csharp
public record GatewayQuoteContext
{
    public string? MsgType { get; init; }           // text / image / mixed / voice / file / video
    public IReadOnlyList<GatewayContentPart>? Content { get; init; }
    public string? SourceChannel { get; init; }     // 如 wecom
}
```

示例（企微引用 + 追问）：

```json
{
  "sessionId": "wecom_group_xxx",
  "input": [{ "type": "text", "text": "数据来源是什么" }],
  "quote": {
    "msgType": "text",
    "sourceChannel": "wecom",
    "content": [{ "type": "text", "text": "被引用的原始内容" }]
  }
}
```

### GatewayEvent（出站）

```csharp
public record GatewayEvent
{
    public required GatewayEventObject Object { get; init; }   // Message, Content, Response, Permission, Error
    public required GatewayEventStatus Status { get; init; }   // InProgress, Completed, Failed, Cancelled…
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public GatewayEventData? Data { get; init; }
    public DateTime Timestamp { get; init; }
    public string? SourceType { get; init; }  // 原始 MessageEventType
}
```

### 完成信号契约

| 事件 | 含义 | 渠道侧是否应结束流 |
|------|------|-------------------|
| `StreamComplete` (assistant) | 单轮 assistant 消息完成 | 否 |
| `LoopComplete` / `Response`+`Completed` | 整轮对话结束 | **是** |
| WS `chat.complete` | 同上（WS 包装） | **是** |

## 接口

```csharp
// 单向 Chat 流（HTTP SSE 或 WS 适配器）
public interface IGatewayClient
{
    IAsyncEnumerable<GatewayEvent> ChatAsync(GatewayRequest request, CancellationToken ct = default);
    Task StopChatAsync(string sessionId, CancellationToken ct = default);
    Task<GatewayPermissionRespondResult> RespondPermissionAsync(...);
    Task<IReadOnlyList<GatewayPendingPermission>> GetPendingPermissionsAsync(string sessionId, ...);
}

// 全双工 WebSocket
public interface IGatewayConnection : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task<string> SendChatAsync(GatewayRequest request, CancellationToken ct = default);
    IAsyncEnumerable<GatewayInbound> ReceiveAsync(CancellationToken ct = default);
    Task StopChatAsync(string sessionId, CancellationToken ct = default);
    Task<GatewayPermissionRespondResult> RespondPermissionAsync(...);
}

// IM 渠道实现者
public interface IChannelBridge
{
    string ChannelId { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```

## 事件映射

`GatewayEventMapper` 将 `Seeing.Agent` 的 `IMessageEvent` 映射为 `GatewayEvent`：

| MessageEventType | GatewayEvent |
|------------------|--------------|
| `LoopStart` | `Response` / InProgress |
| `StreamStart` | `Content` / InProgress + `Data.Step` |
| `StreamDelta` | `Content` / InProgress + `Data.Delta` |
| `StreamComplete` (assistant) | `Message` / Completed |
| `StreamComplete` (tool) | `Content` / InProgress |
| `ToolCall*` | `Content` / InProgress |
| `PermissionRequest` | `Permission` / InProgress |
| `LoopComplete` | `Response` / Completed |
| `Error` | `Error` / Failed |

选项 `GatewayEventMapperOptions.FilterThinking`（默认 `true`）可过滤 reasoning 增量。

## Channel 消费助手回复

IM Bridge 应使用 `GatewayAssistantReplyCollector` 累积 assistant 可见文本，并仅在 `LoopComplete` 时结束渠道侧 UI 流。参见 `Channels/GatewayAssistantReplyCollector.cs`。

## WebSocket 帧协议

见 `Protocol/GatewayWsFrame.cs`：

| 帧类型 | 方向 | 说明 |
|--------|------|------|
| `connected` | S→C | 握手 |
| `chat` | C→S | 发起对话（payload = GatewayRequest） |
| `chat.event` | S→C | 流式事件（payload = GatewayEvent） |
| `chat.complete` | S→C | 对话结束 |
| `stop` / `stop.ack` | 双向 | 取消执行 |
| `permission.respond` / `permission.ack` | 双向 | 权限响应 |
| `ping` / `pong` | 双向 | 心跳 |

## 测试

```bash
dotnet test tests/Seeing.Gateway.Tests
```
