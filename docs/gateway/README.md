# Seeing Gateway 兼容层

Gateway 将 **Seeing.Agent 运行时** 与 **外部通讯渠道**（企业微信、钉钉、自定义 Bot 等）解耦，使 Agent 可通过统一协议交互，而不必依赖 Blazor WebUI。

## 架构

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────────┐
│  IM / 自定义渠道 │────▶│ Channel Bridge   │────▶│ Gateway Client SDK  │
│  (企微 WS 等)    │     │ (WeComChannel…)  │     │ HTTP SSE / WebSocket│
└─────────────────┘     └──────────────────┘     └──────────┬──────────┘
                                                             │
                    ┌────────────────────────────────────────▼──────────┐
                    │  Agent + Gateway Host (独立 Kestrel :8765)          │
                    │  AddSeeingGatewayServer → GatewayOrchestrator       │
                    └─────────────────────────────────────────────────────┘
```

Gateway 与 Agent 运行时在**同一进程**内共享 `AgentExecutor` / `SessionManager`；推荐通过 **无头 Gateway.Server** 或 **WebUI dev 联启** 启动，而非依赖插件 DLL。

## 包与职责

| 项目 | NuGet / 路径 | 职责 |
|------|--------------|------|
| [Seeing.Gateway](../../src/Seeing.Gateway/README.md) | `Seeing.Gateway` | 协议模型、`IGatewayClient`、`IChannelBridge`、事件映射 |
| [Seeing.Gateway.Client](../../src/Seeing.Gateway.Client/README.md) | `Seeing.Gateway.Client` | HTTP/SSE 与 WebSocket 客户端 SDK |
| [Seeing.Agent.Gateway](../../src/Seeing.Agent.Gateway/README.md) | `Seeing.Agent.Gateway` | Server：`AddSeeingGatewayServer()` + Kestrel 宿主 |
| [Seeing.Gateway.WeCom](../../src/Seeing.Gateway.WeCom/README.md) | `Seeing.Gateway.WeCom` | 企业微信 AI Bot WebSocket Bridge |

## 示例

| 示例 | 说明 |
|------|------|
| [Gateway Server](../../samples/Seeing.Gateway.Server/README.md) | **推荐**：无头 Agent + Gateway 宿主（生产/联调） |
| [WebUI](../../samples/Seeing.Agent.WebUI/) | 本地 UI 开发，可联启 Gateway（`Enabled: true`） |
| [Console Demo](../../samples/Seeing.Gateway.Console.Demo/README.md) | stdin/stdout 验证 Gateway 全链路 |
| [WeCom Demo](../../samples/Seeing.Gateway.WeCom.Demo/README.md) | 企微 ↔ Gateway ↔ Agent 端到端 |

## 快速启动

### 1. 启动 Agent + Gateway Server（推荐）

```bash
dotnet run --project samples/Seeing.Gateway.Server
```

验证：

```bash
curl http://127.0.0.1:8765/api/gateway/health
```

### 2. 本地开发（WebUI + Gateway 同进程）

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

WebUI 的 `appsettings.json` 已设置 `SeeingAgent:Gateway:Enabled: true`，无需插件 DLL。

### 3. 在自定义宿主中集成

```csharp
builder.Services.AddSeeingAgent(builder.Configuration);
builder.Services.AddSeeingGatewayServer(builder.Configuration);

var app = builder.Build();
await app.Services.InitializeSeeingAgentAsync(workspaceRoot);
await app.RunAsync(); // GatewayHostedService 在 ApplicationStarted 后启动
```

配置示例（`SeeingAgent:Gateway`）：

```json
{
  "Enabled": true,
  "AutoStart": true,
  "Port": 8765,
  "BindAddress": "127.0.0.1",
  "DefaultAgentId": "build",
  "PermissionMode": "auto_approve",
  "EnableWebSocket": true
}
```

<details>
<summary>插件方式（高级，可选）</summary>

仍可通过 `IExtension` 插件加载 `Seeing.Agent.Gateway.dll`（ID: `seeing.agent.gateway`）。示例与文档不再依赖此路径；与 `AddSeeingGatewayServer()` 请勿同时启用。

</details>

### 4. Console 客户端验证

```bash
dotnet run --project samples/Seeing.Gateway.Console.Demo
dotnet run --project samples/Seeing.Gateway.Console.Demo -- --transport ws
```

### 5. 企微 Bridge（可选）

```bash
dotnet run --project samples/Seeing.Gateway.WeCom.Demo
```

## 传输方式

| 传输 | 端点 | 适用场景 |
|------|------|----------|
| HTTP + SSE | `POST /api/gateway/chat` | 脚本、curl、简单 Demo |
| WebSocket | `WS /api/gateway/ws` | Channel Bridge、长连接、权限推送 |

两种传输共享相同的 `GatewayEvent` JSON payload。

## 协议要点

- **入站**：`GatewayRequest`（sessionId、input、agentId…）
- **出站**：`GatewayEvent` 流（Content / Message / Response / Permission / Error）
- **完成信号**：渠道侧「结束回复」应对齐 `LoopComplete` 或 WS `chat.complete`，而非单轮 `StreamComplete`
- **会话 ID**：Client 传入；企微映射为 `wecom_{userid}` 或 `wecom_group_{chatid}`（文件系统安全格式）

## 多入口并存

Gateway 与 WebUI、TUI 并列，共用 `SessionManager` 与 `AgentExecutor`。相同 `sessionId` 的会话可在 WebUI 中查看（需共用 Session 存储路径）。

## 进一步阅读

- [Seeing.Gateway 协议包](../../src/Seeing.Gateway/README.md)
- [Client SDK](../../src/Seeing.Gateway.Client/README.md)
- [Server 集成与 API](../../src/Seeing.Agent.Gateway/README.md)
- [企微对接](../../src/Seeing.Gateway.WeCom/README.md)
