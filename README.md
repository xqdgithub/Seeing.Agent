# Seeing.Agent

一个完整的 AI Agent 框架，支持 Skill、SubAgent、Rules、Hook、Extension 和 MCP 系统。

[![NuGet](https://img.shields.io/nuget/v/Seeing.Agent.svg)](https://www.nuget.org/packages/Seeing.Agent/)

## 特性

- 🤖 **Agent 系统** - 主 Agent / 子 Agent 模式，支持模型配置和系统提示词
- 🛠️ **Tool 工具** - LLM 可调用的工具，JSON Schema 参数验证
- 📝 **注解发现** - 通过 `[Tool]` 注解自动发现和注册工具方法
- 🔌 **MCP 集成** - 自动连接 MCP Server，代理其工具为 ITool
- 🎯 **Skill 技能** - 可复用的能力单元，支持参数传递和上下文
- 🔗 **Hook 钩子** - 18+ 生命周期钩子点，可扩展干预
- 🛡️ **Rules 规则** - 权限控制引擎，支持 Allow/Deny/Ask 三种动作
- 📦 **Extension 扩展** - 简洁的 DI 注册入口

## 安装

```bash
dotnet add package Seeing.Agent
```

## 快速开始

### 1. 注册服务

```csharp
using Seeing.Agent.Extensions;

// 配置来自 ~/.seeing/seeing.json 与项目级 .seeing/seeing.json（appsettings 不参与 SeeingAgent 节）
services.AddSeeingAgent();

// 或使用代码配置（覆盖/补充 seeing.json）
services.AddSeeingAgent(options => 
{
    options.DefaultModel = "gpt-4";
    options.DefaultAgent = "sisyphus";
});
```

### 2. 使用注解定义 Tool（推荐）

```csharp
using Seeing.Agent.Tools.Attributes;

public class WeatherTools
{
    [Tool("获取天气信息")]
    public static async Task<string> GetWeather(
        [ToolParam("城市名")] string city,
        [ToolParam("日期")] [Required] DateTime date)
    {
        return $"城市: {city}, 日期: {date:yyyy-MM-dd}, 天气: 晴";
    }
    
    [Tool("获取气温", Name = "get_temperature")]
    public static async Task<double> GetTemperature(
        [ToolParam("城市名")] string city)
    {
        return 25.5;
    }
}

// 注册工具
var toolInvoker = serviceProvider.GetRequiredService<ToolInvoker>();
toolInvoker.RegisterToolsFromType<WeatherTools>();
```

### 3. 实现 ITool 接口（手动方式）

```csharp
using Seeing.Agent.Core.Interfaces;
using System.Text.Json;

public class MyTool : ITool
{
    public string Id => "my_tool";
    public string Description => "示例工具";
    
    public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            input = new { type = "string", description = "输入文本" }
        },
        required = new[] { "input" }
    });
    
    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var input = arguments.GetProperty("input").GetString();
        return new ToolResult
        {
            Success = true,
            Title = "工具执行结果",
            Output = $"处理: {input}"
        };
    }
}
```

### 4. 统一工具调用

```csharp
using Seeing.Agent.Tools;
using Seeing.Agent.Core.Models;

var toolInvoker = serviceProvider.GetRequiredService<ToolInvoker>();

// 方式1: 使用 ToolCall 对象
var result = await toolInvoker.ExecuteAsync(new ToolCall
{
    Name = "GetWeather",
    Id = "call-001",
    Arguments = JsonSerializer.SerializeToElement(new { city = "北京", date = DateTime.Now })
});

// 方式2: 使用字典参数
var result = await toolInvoker.ExecuteAsync("GetWeather", new Dictionary<string, object?>
{
    ["city"] = "北京",
    ["date"] = DateTime.Now
});

// 获取工具 Schema（用于 LLM function calling）
var schemas = toolInvoker.GetToolSchemas();
```

### 5. 连接 MCP Server

```csharp
using Seeing.Agent.MCP;

var mcpManager = serviceProvider.GetRequiredService<McpClientManager>();

// 连接单个 MCP Server
await mcpManager.ConnectAsync(new McpServerConfig
{
    Name = "filesystem",
    Command = "npx",
    Args = new List<string> { "-y", "@modelcontextprotocol/server-filesystem", "/path/to/files" }
});

// 批量连接
await mcpManager.ConnectAsync(new[]
{
    new McpServerConfig { Name = "github", Command = "mcp-github", Args = new List<string>() },
    new McpServerConfig { Name = "brave-search", Command = "mcp-brave-search", Args = new List<string>() }
});

// 获取 MCP 工具并注册到 ToolInvoker
var mcpTools = mcpManager.GetTools();
toolInvoker.RegisterTools(mcpTools);
```

### 6. 定义 Skill

```csharp
using Seeing.Agent.Core.Interfaces;

public class MySkill : ISkill
{
    public string Name => "my_skill";
    public string Description => "示例技能";
    public string Location => "skills/my_skill.md";
    
    public async Task<SkillResult> ExecuteAsync(SkillContext context, CancellationToken cancellationToken = default)
    {
        return new SkillResult
        {
            Success = true,
            Output = "技能执行完成"
        };
    }
}
```

### 7. 使用 Hook

```csharp
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Hooks;

public class LogHook : IHookHandler
{
    public string HookPoint => HookPoints.ToolExecuteBefore;
    public int Priority => 10;
    
    public async Task<HookResult> ExecuteAsync(HookContext context)
    {
        var toolId = context.Data.TryGetValue("toolId", out var id) ? id : "unknown";
        Console.WriteLine($"[Hook] 工具即将执行: {toolId}");
        return new HookResult { Continue = true };
    }
}

// 在 DI 中注册
services.AddSingleton<IHookHandler, LogHook>();
```

### 8. 配置权限规则

```csharp
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Rules;

var ruleEngine = serviceProvider.GetRequiredService<RuleEngine>();

ruleEngine.AddRule(new PermissionRule
{
    Permission = "file_write",
    Pattern = "/safe/path/*",
    Action = PermissionAction.Allow
});

ruleEngine.AddRule(new PermissionRule
{
    Permission = "file_write",
    Pattern = "/system/*",
    Action = PermissionAction.Deny
});

ruleEngine.AddRule(new PermissionRule
{
    Permission = "tool",
    Pattern = "dangerous_*",
    Action = PermissionAction.Ask  // 需要用户确认
});
```

## 注解属性

| 属性 | 用途 | 目标 |
|------|------|------|
| `[Tool(description)]` | 标记方法为工具 | Method |
| `[ToolParam(description)]` | 参数描述 | Parameter |
| `[Required]` | 标记必需参数 | Parameter |
| `[ToolParamType(description)]` | 类型/属性描述 | Class/Property |

## Hook 点列表

| Hook 点 | 触发时机 | 可修改数据 |
|---------|----------|------------|
| `chat.before_start` | LLM 请求开始前 | - |
| `chat.after_complete` | LLM 请求完成后 | response |
| `chat.before_retry` | LLM 调用重试前 | - |
| `chat.on_error` | LLM 调用异常时 | - |
| `chat.message` | 收到 LLM 响应消息 | message, role |
| `chat.params` | LLM 请求参数设置 | temperature, topP, maxTokens |
| `chat.headers` | LLM 请求 Headers | headers |
| `tool.before_register` | 工具注册前（可拦截） | toolId, description |
| `tool.after_register` | 工具注册后 | - |
| `tool.definition` | 获取工具 Schema 时 | description, parameters |
| `tool.execute.before` | 工具执行前 | args |
| `tool.execute.after` | 工具执行后 | title, output, metadata |
| `tool.on_error` | 工具执行异常时 | - |
| `session.created` | Session 创建时 | - |
| `session.updated` | Session 更新时 | - |
| `session.deleted` | Session 删除时 | - |
| `session.compacting` | Session 压缩前 | - |
| `session.idle` | Session 进入空闲状态 | - |
| `session.error` | Session 发生错误 | error |
| `agent.before_invoke` | Agent 执行前 | - |
| `agent.after_invoke` | Agent 执行后 | - |
| `permission.ask` | 权限 Ask 决策时 | decision, reason |
| `llm.system_prompt` | 系统提示词设置 | prompt |
| `shell.env` | Shell 命令执行前 | env |
| `command.execute.before` | 自定义命令执行前 | arguments, proceed |

## 项目结构

```
Seeing.Agent/
├── Core/
│   ├── Interfaces/    # IAgent, ISkill, ITool, IHook, IRuleEngine
│   ├── Abstractions/  # AgentBase, SkillBase, ToolBase
│   └── Models/        # ChatMessage, ConfigurationModels
├── Tools/
│   ├── Attributes/    # Tool, ToolParam, Required 注解
│   ├── Discovery/     # ToolDiscovery, ReflectedTool
│   ├── ToolInvoker.cs # 统一工具调用器
│   └── ToolRegistry.cs
├── MCP/
│   ├── McpTool.cs         # MCP 工具包装器
│   └── McpClientManager.cs # MCP 客户端管理器
├── Hooks/             # HookManager
├── Skills/            # SkillManager
├── Sessions/          # SessionManager
├── Shell/             # ShellEnvironmentService (shell.env Hook)
├── Commands/          # CommandService (command.execute.before Hook)
├── Rules/             # RuleEngine
├── Configuration/     # SeeingAgentOptions
└── Extensions/        # ServiceCollectionExtensions

Gateway 兼容层（Agent 外部通讯）→ 详见 [docs/gateway/README.md](docs/gateway/README.md)
├── src/Seeing.Gateway/           # 协议模型与事件映射
├── src/Seeing.Gateway.Client/      # HTTP/SSE + WebSocket 客户端 SDK
├── src/Seeing.Agent.Gateway/       # Server 插件（独立 Kestrel）
├── src/Seeing.Gateway.WeCom/       # 企业微信 Channel Bridge
├── samples/Seeing.Gateway.Console.Demo/
├── samples/Seeing.Gateway.Server/    # 无头 Agent+Gateway 宿主（推荐）
└── samples/Seeing.Gateway.WeCom.Demo/
```

## 配置选项

Seeing.Agent 从 **用户级** `~/.seeing/seeing.json` 与 **项目级** `./.seeing/seeing.json` 加载配置；`appsettings.json` 仅用于宿主日志等，不再合并 `SeeingAgent` 节。

```json
{
  "SeeingAgent": {
    "DefaultModel": "openai/gpt-4",
    "DefaultAgent": "sisyphus",
    "SkillPaths": [ "./skills", "./.agents/skills" ],
    "Providers": {
      "openai": {
        "BaseUrl": "https://api.openai.com/v1",
        "Timeout": 60,
        "MaxRetries": 3
      }
    },
    "Agents": {
      "sisyphus": {
        "Runtime": "Native",
        "SystemPrompt": "You are a helpful assistant.",
        "MaxSteps": 50
      }
    }
  }
}
```

## Gateway 外部通讯

除 Blazor WebUI 外，可通过 **Gateway 兼容层** 对接企业微信等 IM 渠道，或自建 Channel Bridge。

| 文档 | 说明 |
|------|------|
| [Gateway 总览](docs/gateway/README.md) | 架构、快速启动、协议要点 |
| [Seeing.Gateway](src/Seeing.Gateway/README.md) | 协议 DTO 与事件映射 |
| [Seeing.Gateway.Client](src/Seeing.Gateway.Client/README.md) | Client SDK（HTTP SSE / WebSocket） |
| [Seeing.Agent.Gateway](src/Seeing.Agent.Gateway/README.md) | Server 集成（`AddSeeingGatewayServer`）与 API |
| [Gateway Server](samples/Seeing.Gateway.Server/README.md) | 无头 Agent+Gateway 宿主（推荐） |
| [Seeing.Gateway.WeCom](src/Seeing.Gateway.WeCom/README.md) | 企业微信 Bridge |
| [Console Demo](samples/Seeing.Gateway.Console.Demo/README.md) | 本地 Gateway 联调 |
| [WeCom Demo](samples/Seeing.Gateway.WeCom.Demo/README.md) | 企微端到端联调 |

```bash
# 启动 Agent + Gateway（:8765，推荐无头宿主）
dotnet run --project samples/Seeing.Gateway.Server

# 或本地 UI 开发时联启 Gateway
dotnet run --project samples/Seeing.Agent.WebUI

# Console 客户端验证
dotnet run --project samples/Seeing.Gateway.Console.Demo -- --transport ws

# 企微 Bridge（需配置 BotId/Secret）
dotnet run --project samples/Seeing.Gateway.WeCom.Demo
```

## 许可证

MIT License