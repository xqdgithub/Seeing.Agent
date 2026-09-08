# Gateway 文档

Gateway 是 Seeing.Agent 的外部通讯族（协议、客户端、通道、服务端集成）。

## 在本仓库中的位置

| 包 | 路径 |
|----|------|
| 协议 | `src/gateway/Seeing.Gateway/` |
| 客户端 | `src/gateway/Seeing.Gateway.Client/` |
| WeCom / QQ 通道 | `src/gateway/Seeing.Gateway.WeCom/`、`...QQ/` |
| Agent 集成 | `src/gateway/Seeing.Agent.Gateway/` |
| Host Shape | `src/hosting/Seeing.Agent.Hosting.Gateway/`（**不**引用集成包） |

模块地图与依赖规则见 [架构 · 02 模块地图](../architecture/02-modules.md)。  
分层与 Host Shape 组合见 [架构 · 01 总览](../architecture/01-overview.md)。

## 快速启动

```bash
# 推荐：无头 Agent + Gateway（:8765）
dotnet run --project samples/Seeing.Gateway.Server

# Sample 须同时：AddSeeingHostingGateway() + AddSeeingGatewayServer(...)
```

## 包内 README

- [Seeing.Gateway](../../src/gateway/Seeing.Gateway/README.md)
- [Seeing.Gateway.Client](../../src/gateway/Seeing.Gateway.Client/README.md)
- [Seeing.Agent.Gateway](../../src/gateway/Seeing.Agent.Gateway/README.md)
- [Seeing.Gateway.WeCom](../../src/gateway/Seeing.Gateway.WeCom/README.md)

## 硬规则（防回潮）

- `Seeing.Agent.Gateway` **不得** ProjectReference Core / Hosting  
- `Seeing.Agent.Hosting.Gateway` **不得** ProjectReference `Seeing.Agent.Gateway`  
- 通道包只依赖 Abstractions + 协议；工作区经 `IWorkspaceProvider`
