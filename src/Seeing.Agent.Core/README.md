# Seeing.Agent

全功能 AI Agent 框架，提供 Agent 编排、工具发现、权限控制、MCP 协议集成等核心能力。

## 快速开始

```csharp
// Program.cs
builder.Services.AddSeeingAgent(options =>
{
    options.DefaultModel = "gpt-4o";
});

// 注册自定义工具
builder.Services.AddToolsFromType<MyTools>();
```

## 核心功能

| 功能 | 说明 |
|------|------|
| Agent 编排 | 多 Agent 协作、子 Agent 委托、Plan 模式 |
| 工具系统 | 注解驱动注册、内置 10+ 工具、MCP 集成 |
| 权限控制 | deny-by-default、规则引擎、多通道支持 |
| 生命周期钩子 | 50+ Hook 点（tool/chat/agent/session/memory） |
| 会话管理 | 会话分叉/归档/共享、Token 压缩 |
| 扩展插件 | IExtension 接口、程序集热加载 |
| Gateway | QQ/企业微信/HTTP/WebSocket 多通道 |

## 依赖

- .NET 10.0
- Seeing.Session（会话管理）
- Seeing.TokenEstimation（Token 估算）

## 配置

配置文件使用 `.seeing/seeing.json`（项目级）和 `~/.seeing/seeing.json`（用户级），自动深度合并。
