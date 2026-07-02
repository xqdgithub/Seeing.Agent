# Seeing.Gateway.Server

无头 **Agent + Gateway** 宿主，用于生产部署、企微 Bridge、Console 客户端联调。

## 启动

```bash
dotnet run --project samples/Seeing.Gateway.Server
```

验证：

```bash
curl http://127.0.0.1:8765/api/gateway/health
```

## 配置

`appsettings.json` → `SeeingAgent:Gateway`：

| 字段 | 说明 |
|------|------|
| `Enabled` | 是否启用 Gateway（默认 `true`） |
| `AutoStart` | 宿主启动后自动监听（默认 `true`） |
| `Port` / `BindAddress` | 监听地址 |
| `DefaultAgentId` | 默认 Agent（如 `build`） |
| `PermissionMode` | 无头场景推荐 `auto_approve` |

LLM Provider / MCP 等配置从 `~/.seeing/seeing.json` 或项目 `.seeing/seeing.json` 加载（与 WebUI 相同）。

## 与 WebUI 的关系

- **Gateway.Server**：推荐的生产/无头入口
- **WebUI**：本地 UI 开发，可通过 `Enabled: true` 联启 Gateway（dev 模式）
- 两者共用 `SessionManager` 存储路径时，WebUI 可查看 Gateway 创建的会话

## 客户端

```bash
dotnet run --project samples/Seeing.Gateway.Console.Demo
dotnet run --project samples/Seeing.Gateway.WeCom.Demo
```
