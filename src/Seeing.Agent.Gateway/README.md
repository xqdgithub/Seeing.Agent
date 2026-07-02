# Seeing.Agent.Gateway

**Gateway Server**：在 Agent 进程内启动独立 Kestrel，将 `AgentExecutor` 能力以 HTTP+SSE / WebSocket 协议暴露。

## 推荐集成方式

```csharp
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Extensions;

builder.Services.AddSeeingAgent(builder.Configuration);
builder.Services.AddSeeingGatewayServer(builder.Configuration);

var host = builder.Build();
await host.Services.InitializeSeeingAgentAsync(workspaceRoot);
await host.RunAsync();
```

`AddSeeingGatewayServer` 注册：

| 类型 | 职责 |
|------|------|
| `IGatewayServer` / `GatewayServer` | 管理 `GatewayHost` 生命周期 |
| `GatewayHostedService` | `ApplicationStarted` 后自动启动（需 `Enabled` + `AutoStart`） |

**启动顺序**：必须先 `InitializeSeeingAgentAsync`，再 `Run()` / `RunAsync()`。

## 配置（`SeeingAgent:Gateway`）

| 字段 | 默认 | 说明 |
|------|------|------|
| `Enabled` | `false` | 是否启用 Gateway |
| `AutoStart` | `true` | 宿主启动后自动监听 |
| `Port` | `8765` | 监听端口 |
| `BindAddress` | `127.0.0.1` | 绑定地址 |
| `DefaultAgentId` | — | 默认 Agent |
| `PermissionMode` | `interactive` | 无头场景用 `auto_approve` |
| `EnableWebSocket` | `true` | 启用 WS 端点 |
| `WebSocketPath` | `/api/gateway/ws` | WS 路径 |

## 示例宿主

| 样本 | 场景 |
|------|------|
| [Seeing.Gateway.Server](../../samples/Seeing.Gateway.Server/) | 生产/无头（**推荐**） |
| [Seeing.Agent.WebUI](../../samples/Seeing.Agent.WebUI/) | 本地 UI + Gateway 联启 |

```bash
dotnet run --project samples/Seeing.Gateway.Server
curl http://127.0.0.1:8765/api/gateway/health
```

## HTTP API

### POST `/api/gateway/chat`

请求体：`GatewayRequest`  
响应：`text/event-stream`，每行 `data: {GatewayEvent JSON}`

```bash
curl -N -X POST http://127.0.0.1:8765/api/gateway/chat \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{"sessionId":"test","input":[{"type":"text","text":"hello"}],"stream":true}'
```

### WebSocket `/api/gateway/ws`

1. 连接后收到 `connected` 帧
2. 发送 `chat` 帧（payload = GatewayRequest）
3. 接收 `chat.event` 流式事件
4. 收到 `chat.complete` 表示本轮结束

### POST `/api/gateway/chat/stop?sessionId=`

取消指定会话的活跃执行。

### 权限（interactive 模式）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/gateway/permissions/pending?sessionId=` | 轮询待确认权限 |
| POST | `/api/gateway/permissions/{id}/respond` | 批准/拒绝 |

### GET `/api/gateway/health`

健康检查。

## 内部组件

| 组件 | 职责 |
|------|------|
| `GatewayServer` | `IGatewayServer` 实现，封装 Kestrel 启停 |
| `GatewayHostedService` | 随 Generic Host 自动启动 |
| `GatewayHost` | 独立 WebApplication 宿主 |
| `GatewayOrchestrator` | 镜像 WebUI 执行流 |
| `SessionExecutionQueue` | 同 `(channelId, sessionId)` 串行 |
| `GatewayPermissionChannel` | Gateway 专用权限通道 |
| `GatewayWebSocketHandler` | WS 帧分发 |

## 插件方式（可选）

`GatewayExtension`（ID: `seeing.agent.gateway`）仍可作为 `IExtension` 插件加载。与 `AddSeeingGatewayServer()` **请勿同时启用**。

## 与 WebUI 的关系

- Gateway 与 Blazor WebUI **并列**，不替代 WebUI
- 同进程时共用 `SessionManager`
- WebUI 使用 `BlazorPermissionChannel`；Gateway 使用 `GatewayPermissionChannel`

## 安全说明

首版默认 `BindAddress: 127.0.0.1`。生产环境请绑定内网并增加 API Key 中间件（待实现）。
