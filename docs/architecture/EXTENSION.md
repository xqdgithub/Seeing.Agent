# Seeing.Agent Extension 扩展系统

完整的插件化扩展机制，支持动态加载外部扩展以提供额外的 Agent、Tool、Hook、MCP Server 和 Skill 能力。

---

## 快速开始

### 1. 创建扩展项目

```xml
<!-- MyExtension.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- 引用 Seeing.Agent（共享接口） -->
    <ProjectReference Include="path/to/Seeing.Agent.csproj" 
                      Private="false" 
                      ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

### 2. 实现 IExtension 接口

```csharp
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace MyExtension
{
    public class MyExtension : IExtension
    {
        public string? Id => "my-extension";
        public string Version => "1.0.0";
        public string Name => "我的扩展";
        public string Description => "提供自定义能力";

        private readonly List<IAgent> _agents = new();
        private readonly List<ITool> _tools = new();

        public void ConfigureServices(IServiceCollection services)
        {
            // 注册扩展内部依赖
            services.AddSingleton<MyService>();
        }

        public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
        {
            var logger = context.Services.GetRequiredService<ILogger<MyExtension>>();
            logger.LogInformation("初始化扩展: {Name} v{Version}", Name, Version);

            // 创建 Agent
            _agents.Add(new MyCustomAgent(
                context.Services.GetRequiredService<ILogger<MyCustomAgent>>()));

            // 创建工具
            _tools.Add(new MyCustomTool(
                context.Services.GetRequiredService<ILogger<MyCustomTool>>()));
        }

        public IEnumerable<IAgent> GetAgents() => _agents;
        public IEnumerable<ITool> GetTools() => _tools;

        public async Task DisposeAsync()
        {
            _agents.Clear();
            _tools.Clear();
            await Task.CompletedTask;
        }
    }
}
```

### 3. 创建自定义 Agent

```csharp
public class MyCustomAgent : AgentBase
{
    public MyCustomAgent(ILogger logger) : base(logger) { }

    public override string Name => "my-custom-agent";
    public override string Description => "自定义代理";
    public override AgentMode Mode => AgentMode.SubAgent;
    public override int? MaxSteps => 30;

    public override string? SystemPrompt => """
        你是一个自定义代理，专注于...
        """;

    protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
        ChatMessage input,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "处理完成"
        };
    }
}
```

### 4. 配置加载

在 `seeing.json` 中声明扩展：

```json
{
  "SeeingAgent": {
    "Plugins": [
      "./plugins/MyExtension.dll",
      ["./plugins/AnotherExtension.dll", { "logLevel": "Debug" }]
    ],
    "PluginEnabled": {
      "my-extension": true,
      "another-extension": false
    }
  }
}
```

---

## 配置说明

### 配置文件位置

| 级别 | 路径 | 说明 |
|------|------|------|
| **用户级** | `~/.seeing/seeing.json` | 全局配置，所有项目共享 |
| **项目级** | `<workspace>/.seeing/seeing.json` | 项目专属配置，**覆盖用户级** |

### 配置格式

```json
{
  "SeeingAgent": {
    "Plugins": [
      // 字符串格式
      "./plugins/MyExtension.dll",
      
      // 用户主目录格式
      "~/.seeing/plugins/MyExtension.dll",
      
      // 带选项格式
      ["./plugins/MyExtension.dll", { 
        "logLevel": "Debug",
        "customOption": "value"
      }],
      
      // file:// URL 格式
      "file://./plugins/MyExtension.dll"
    ],
    "PluginEnabled": {
      "my-extension": true,
      "disabled-extension": false
    }
  }
}
```

### PluginSpec 格式

| 格式 | 示例 | 说明 |
|------|------|------|
| 相对路径 | `./plugins/MyExtension.dll` | 相对于配置文件位置 |
| 绝对路径 | `/path/to/MyExtension.dll` | 完整文件路径 |
| 用户主目录 | `~/.seeing/plugins/MyExtension.dll` | `~` 展开为用户主目录 |
| file:// URL | `file://./plugins/MyExtension.dll` | 文件 URL 格式 |

---

## IExtension 接口

### 元数据属性

```csharp
public interface IExtension
{
    // 唯一标识（可选，默认使用文件名）
    string? Id => null;
    
    // 版本号
    string Version => "1.0.0";
    
    // 显示名称
    string Name => "";
    
    // 描述
    string Description => "";
    
    // 目标运行时（当前仅支持 server）
    string Target => "server";
}
```

### 生命周期方法

```csharp
public interface IExtension
{
    // 注册服务（DI 容器构建前调用）
    void ConfigureServices(IServiceCollection services) { }
    
    // 初始化（服务容器构建后调用）
    Task InitializeAsync(ExtensionContext context, ExtensionMeta meta) 
        => Task.CompletedTask;
    
    // 清理资源（停用时调用）
    Task DisposeAsync() => Task.CompletedTask;
}
```

### 组件提供方法

```csharp
public interface IExtension
{
    // 提供 Agent
    IEnumerable<IAgent> GetAgents() => Enumerable.Empty<IAgent>();
    
    // 提供工具
    IEnumerable<ITool> GetTools() => Enumerable.Empty<ITool>();
    
    // 提供 Hook 处理器
    IEnumerable<IHookHandler> GetHookHandlers() => Enumerable.Empty<IHookHandler>();
    
    // 提供 MCP Server 配置
    IEnumerable<McpServerConfig> GetMcpServers() => Enumerable.Empty<McpServerConfig>();
    
    // 提供 Skill 搜索路径
    IEnumerable<string> GetSkillPaths() => Enumerable.Empty<string>();
}
```

---

## ExtensionContext 上下文

提供给扩展的运行时信息和服务引用：

```csharp
public class ExtensionContext
{
    // 服务提供者
    public IServiceProvider Services { get; set; }
    
    // 配置
    public IConfiguration Configuration { get; set; }
    
    // 当前工作目录
    public string Directory { get; set; }
    
    // 工作区根目录
    public string WorkspaceRoot { get; set; }

    // 核心服务引用
    public HookManager HookManager { get; set; }
    public ToolInvoker ToolInvoker { get; set; }
    public RuleEngine RuleEngine { get; set; }
    public SkillManager SkillManager { get; set; }
    public IAgentRegistry AgentRegistry { get; set; }
    public McpClientManager McpClientManager { get; set; }
}
```

---

## ExtensionMeta 元数据

扩展加载时的状态信息：

```csharp
public class ExtensionMeta
{
    // 状态：first（首次）、updated（更新）、same（相同）
    public string State { get; set; }
    
    // 扩展 ID
    public string Id { get; set; }
    
    // 来源：file / npm
    public string Source { get; set; }
    
    // 原始 spec
    public string Spec { get; set; }
    
    // 目标路径
    public string Target { get; set; }
    
    // 加载次数
    public int LoadCount { get; set; }
    
    // 时间戳
    public long FirstTime { get; set; }
    public long LastTime { get; set; }
}
```

---

## 启动流程集成

```csharp
// Program.cs

var services = new ServiceCollection();

// 1. 注册核心服务
services.AddSeeingAgent(configuration);

// 2. 构建 ServiceProvider
var provider = services.BuildServiceProvider();

// 3. 构建扩展上下文
var context = new ExtensionContext
{
    Services = provider,
    Configuration = configuration,
    Directory = Directory.GetCurrentDirectory(),
    WorkspaceRoot = workspaceRoot,
    HookManager = provider.GetRequiredService<HookManager>(),
    ToolInvoker = provider.GetRequiredService<ToolInvoker>(),
    RuleEngine = provider.GetRequiredService<RuleEngine>(),
    SkillManager = provider.GetRequiredService<SkillManager>(),
    AgentRegistry = provider.GetRequiredService<IAgentRegistry>(),
    McpClientManager = provider.GetRequiredService<McpClientManager>()
};

// 4. 加载扩展配置（通过 UnifiedConfigManager）
var configManager = provider.GetRequiredService<UnifiedConfigManager>();
await configManager.LoadAsync();
var options = configManager.GetSeeingAgentOptions();
var pluginSpecs = options.Plugins;
var enabledOverrides = options.PluginEnabled;

// 5. 初始化扩展系统
var extensionManager = provider.GetRequiredService<ExtensionManager>();
await extensionManager.InitializeAsync(pluginSpecs, enabledOverrides, context);

// 6. 运行应用...
```

---

## API 参考

### ExtensionManager

| 方法 | 说明 |
|------|------|
| `InitializeAsync(specs, enabledOverrides, context)` | 初始化并加载所有扩展 |
| `GetAll()` | 获取所有已加载的扩展 |
| `Get(id)` | 获取指定扩展 |
| `ListStatus()` | 获取扩展状态列表 |
| `ActivateAsync(id, context)` | 激活扩展 |
| `DeactivateAsync(id)` | 停用扩展 |
| `AddAsync(spec, context)` | 动态添加扩展 |
| `DisposeAllAsync()` | 清理所有扩展 |

### ExtensionLoader

| 方法 | 说明 |
|------|------|
| `ResolveTarget(spec)` | 解析插件 spec 为程序集路径 |
| `LoadFromAssembly(path)` | 从程序集加载扩展实例 |
| `LoadExternal(specs, context)` | 加载外部插件 |

### UnifiedConfigManager（扩展配置相关）

| 方法/属性 | 说明 |
|------|------|
| `GetSeeingAgentOptions().Plugins` | 获取插件列表 |
| `GetSeeingAgentOptions().PluginEnabled` | 获取插件启用状态字典 |
| `SaveSectionAsync("Plugins", value, level)` | 保存插件配置 |
| `SaveSectionAsync("PluginEnabled", value, level)` | 保存启用状态 |
| `LoadEnabledOverrides(userPath, projectPath, logger)` | 加载启用状态覆盖 |
| `GetDefaultPaths(workspaceRoot)` | 获取默认配置路径 |

---

## 完整示例

```csharp
// AnalyticsExtension.cs
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Plugin.Analytics
{
    public class AnalyticsExtension : IExtension
    {
        public string? Id => "@seeing/analytics";
        public string Version => "1.0.0";
        public string Name => "Code Analytics";
        public string Description => "提供代码分析能力";

        private readonly List<IAgent> _agents = new();
        private readonly List<ITool> _tools = new();

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<CodeAnalyzerService>();
        }

        public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
        {
            var logger = context.Services.GetRequiredService<ILogger<AnalyticsExtension>>();
            var analyzer = context.Services.GetRequiredService<CodeAnalyzerService>();

            _agents.Add(new CodeAnalyzerAgent(
                context.Services.GetRequiredService<ILogger<CodeAnalyzerAgent>>(),
                analyzer));

            _tools.Add(new MetricsTool(
                context.Services.GetRequiredService<ILogger<MetricsTool>>()));

            await Task.CompletedTask;
        }

        public IEnumerable<IAgent> GetAgents() => _agents;
        public IEnumerable<ITool> GetTools() => _tools;

        public async Task DisposeAsync()
        {
            _agents.Clear();
            _tools.Clear();
            await Task.CompletedTask;
        }
    }

    // CodeAnalyzerAgent.cs
    public class CodeAnalyzerAgent : AgentBase
    {
        private readonly CodeAnalyzerService _analyzer;

        public CodeAnalyzerAgent(ILogger logger, CodeAnalyzerService analyzer)
            : base(logger)
        {
            _analyzer = analyzer;
        }

        public override string Name => "code-analyzer";
        public override string Description => "代码分析专家";
        public override AgentMode Mode => AgentMode.SubAgent;

        protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
            ChatMessage input,
            AgentContext context,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var result = await _analyzer.AnalyzeAsync(input.Content ?? "", ct);
            yield return new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = result
            };
        }
    }
}
```

---

## 目录结构

```
~/.seeing/
├── seeing.json           # 用户级配置
└── plugins/              # 用户级插件

<workspace>/
├── .seeing/
│   ├── seeing.json       # 项目级配置
│   └── plugins/          # 项目级插件
│       └── MyExtension.dll
└── ...
```

---

## 注意事项

1. **程序集隔离**：扩展使用独立的 `AssemblyLoadContext` 加载，支持卸载
2. **共享类型**：核心接口（`IExtension`、`IAgent`、`ITool` 等）在主上下文中加载，确保类型身份一致
3. **依赖注入**：扩展可以使用构造函数注入，通过 `ConfigureServices` 注册服务
4. **错误隔离**：单个扩展加载失败不会影响其他扩展
5. **选项传递**：配置中的 `Options` 会传递给 `InitializeAsync` 的扩展实例

---

## 参考

- 参考 opencode Plugin 系统设计
- 参考 Microsoft.Extensions.DependencyInjection 模式
- 参考 ASP.NET Core IStartup 模式