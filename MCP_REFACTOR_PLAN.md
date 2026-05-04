# MCP 热加载优化执行计划

**目标**: 实现 MCP 非阻塞启动、状态追踪、自动重连、热加载、WebUI 状态展示

---

## Phase 1: 基础模型层（并行执行）

**预计时间**: 2小时  
**可并行**: 6 个任务可同时执行

---

### Task 1.1: MCP Core 状态模型

**路径**: `src/Seeing.Agent/MCP/Core/`

**创建文件列表**:

| 文件名 | 内容 |
|--------|------|
| `McpConnectionState.cs` | 连接状态枚举：Pending/Connecting/Connected/Paused/Reconnecting/Error/Removed |
| `McpServerPriority.cs` | Server 优先级枚举：Critical/High/Normal/Low |
| `McpErrorCode.cs` | 错误码枚举：ConnectionTimeout/AuthenticationFailed/ProcessCrashed 等 |
| `McpErrorInfo.cs` | 分层错误信息模型：Code/TechnicalDetail/UserMessage/RecoveryHint/IsTransient |
| `McpOperationResult.cs` | 操作结果模型：Success/ServerName/OperationType/Status/Error/Duration |
| `McpOperationType.cs` | 操作类型枚举：Connect/Disconnect/Reconnect/Pause/Resume/Add/Remove 等 |
| `McpServerStatus.cs` | 不可变状态模型（sealed class，私有构造器） |
| `McpServerStatusBuilder.cs` | 状态构建器（Builder 模式） |
| `McpStateTransitions.cs` | 状态转换矩阵（internal static class） |
| `IMcpStatusProvider.cs` | 状态查询接口 |
| `IMcpController.cs` | 控制操作接口 |
| `IMcpConfigManager.cs` | 配置管理接口 |
| `IMcpManager.cs` | 组合接口（继承三个子接口 + IAsyncDisposable） |
| `McpNotConnectedError.cs` | 预定义错误工厂方法 |

**关键实现点**:

```csharp
// McpConnectionState.cs - 状态枚举
public enum McpConnectionState
{
    Pending,        // 待连接
    Connecting,     // 连接中
    Connected,      // 已连接
    Paused,         // 已暂停
    Reconnecting,   // 重连中
    Error,          // 错误
    Removed         // 已移除
}

// McpServerStatus.cs - 不可变状态
public sealed class McpServerStatus
{
    public string Name { get; }
    public McpConnectionState State { get; }
    public McpServerConfig Config { get; }
    public int ToolCount { get; }
    public IReadOnlyList<string> ToolNames { get; }
    public DateTime? LastConnectedAt { get; }
    public DateTime? LastErrorAt { get; }
    public McpErrorInfo? LastError { get; }
    public int ReconnectAttempts { get; }
    
    // 私有构造器，只能通过 Builder 创建
    internal McpServerStatus(McpServerStatusBuilder builder) { ... }
    
    public McpServerStatus Clone() { ... }
    
    // 派生属性
    public bool IsAvailable => State == McpConnectionState.Connected;
    public bool CanReconnect => State == McpConnectionState.Error && ReconnectAttempts < MaxAttempts;
}

// McpStateTransitions.cs - 状态转换矩阵
internal static class McpStateTransitions
{
    private static readonly Dictionary<McpConnectionState, McpConnectionState[]> Allowed = new()
    {
        [McpConnectionState.Pending] = new[] { Connecting, Paused, Removed },
        [McpConnectionState.Connecting] = new[] { Connected, Error, Paused },
        // ... 其他状态转换规则
    };
    
    public static bool CanTransition(McpConnectionState from, McpConnectionState to);
    public static void ValidateTransition(McpConnectionState from, McpConnectionState to);
}
```

**验证命令**:
```bash
dotnet build src/Seeing.Agent --no-restore
```

---

### Task 1.2: MCP Policy 策略模型

**路径**: `src/Seeing.Agent/MCP/Policy/`

**创建文件列表**:

| 文件名 | 内容 |
|--------|------|
| `McpReconnectionPolicy.cs` | 重连策略：Enabled/MaxAttempts/InitialInterval/MaxInterval/BackoffMultiplier |
| `McpGlobalPolicy.cs` | 全局策略：DefaultReconnectionPolicy/ConnectionTimeout/MaxConcurrentConnections |
| `McpServerPolicyOverride.cs` | Server 级策略覆盖配置 |

**关键实现点**:

```csharp
// McpReconnectionPolicy.cs
public sealed class McpReconnectionPolicy
{
    public bool Enabled { get; init; } = true;
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromSeconds(60);
    public double BackoffMultiplier { get; init; } = 2.0;
    
    // 计算下次重连间隔（指数退避）
    public TimeSpan CalculateInterval(int attempt)
    {
        var interval = InitialInterval * Math.Pow(BackoffMultiplier, attempt - 1);
        return interval > MaxInterval ? MaxInterval : interval;
    }
    
    // 预定义策略
    public static McpReconnectionPolicy Default => new();
    public static McpReconnectionPolicy Fast => new() { MaxAttempts = 10, InitialInterval = TimeSpan.FromSeconds(1) };
    public static McpReconnectionPolicy Conservative => new() { MaxAttempts = 3, InitialInterval = TimeSpan.FromSeconds(5) };
}

// McpGlobalPolicy.cs
public sealed class McpGlobalPolicy
{
    public McpReconnectionPolicy DefaultReconnectionPolicy { get; init; } = McpReconnectionPolicy.Default;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BackgroundCheckInterval { get; init; } = TimeSpan.FromSeconds(10);
    public bool AutoStartOnAdd { get; init; } = true;
}
```

---

### Task 1.3: MCP Factory 工厂注册

**路径**: `src/Seeing.Agent/MCP/Factory/`

**创建文件列表**:

| 文件名 | 内容 |
|--------|------|
| `IMcpClientWrapperFactory.cs` | 传输类型工厂接口 |
| `McpWrapperFactoryRegistry.cs` | 工厂注册表（支持扩展新传输类型） |
| `StdioWrapperFactory.cs` | stdio 传输工厂实现 |
| `HttpWrapperFactory.cs` | HTTP/SSE 传输工厂实现 |

**关键实现点**:

```csharp
// IMcpClientWrapperFactory.cs
public interface IMcpClientWrapperFactory
{
    McpTransportType TransportType { get; }
    IMcpClientWrapper Create(McpServerConfig config, IHttpClientFactory? httpClientFactory, ILoggerFactory loggerFactory);
}

// McpWrapperFactoryRegistry.cs
public class McpWrapperFactoryRegistry
{
    private readonly Dictionary<McpTransportType, IMcpClientWrapperFactory> _factories = new();
    
    public void Register(IMcpClientWrapperFactory factory)
        => _factories[factory.TransportType] = factory;
    
    public IMcpClientWrapper Create(McpServerConfig config, IHttpClientFactory? httpFactory, ILoggerFactory loggerFactory)
        => _factories.TryGetValue(config.TransportType, out var factory)
            ? factory.Create(config, httpFactory, loggerFactory)
            : throw new NotSupportedException($"传输类型 {config.TransportType} 未注册");
}
```

---

### Task 1.4: MCP Events 事件模型

**路径**: `src/Seeing.Agent/MCP/Events/`

**创建文件列表**:

| 文件名 | 内容 |
|--------|------|
| `McpStatusChangedEventArgs.cs` | 状态变更事件参数：ServerName/Previous/Current/NewState |
| `McpConnectionEvent.cs` | 连接事件（用于 Hook Payload） |

**关键实现点**:

```csharp
// McpStatusChangedEventArgs.cs
public sealed class McpStatusChangedEventArgs : EventArgs
{
    public string ServerName { get; }
    public McpServerStatus? Previous { get; }
    public McpServerStatus Current { get; }
    public McpConnectionState NewState { get; }
    public DateTime Timestamp { get; }
    
    public McpStatusChangedEventArgs(string serverName, McpServerStatus? previous, McpServerStatus current, McpConnectionState newState)
    {
        ServerName = serverName;
        Previous = previous;
        Current = current;
        NewState = newState;
        Timestamp = DateTime.UtcNow;
    }
}
```

---

### Task 1.5: MCP Validation 配置验证

**路径**: `src/Seeing.Agent/MCP/Validation/`

**创建文件列表**:

| 文件名 | 内容 |
|--------|------|
| `McpConfigValidator.cs` | 配置验证器：验证 Name/TransportType/Command/Url |
| `McpValidationResult.cs` | 验证结果：IsValid/Error/Warnings |

**关键实现点**:

```csharp
// McpConfigValidator.cs
public static class McpConfigValidator
{
    public static McpValidationResult Validate(McpServerConfig config)
    {
        var errors = new List<McpErrorInfo>();
        var warnings = new List<string>();
        
        // Name 验证
        if (string.IsNullOrEmpty(config.Name))
            errors.Add(McpErrorInfo.ConfigInvalid("name", "名称不能为空"));
        
        // TransportType 验证
        if (config.TransportType == McpTransportType.Stdio)
        {
            if (string.IsNullOrEmpty(config.Command))
                errors.Add(McpErrorInfo.ConfigInvalid("command", "stdio 传输需要 command"));
            
            if (!File.Exists(config.Command) && !IsCommandInPath(config.Command))
                warnings.Add($"命令可能不存在: {config.Command}");
        }
        else
        {
            if (config.Url == null)
                errors.Add(McpErrorInfo.ConfigInvalid("url", "HTTP 传输需要 url"));
        }
        
        return new McpValidationResult(errors.Count == 0, errors.FirstOrDefault(), warnings);
    }
    
    private static bool IsCommandInPath(string command)
    {
        // 检查命令是否在 PATH 环境变量中
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        return pathDirs?.Any(d => File.Exists(Path.Combine(d, command))) ?? false;
    }
}

// McpValidationResult.cs
public sealed class McpValidationResult
{
    public bool IsValid { get; }
    public McpErrorInfo? Error { get; }
    public IReadOnlyList<string> Warnings { get; }
}
```

---

### Task 1.6: 扩展 HookRegistry MCP Hook 点

**路径**: `src/Seeing.Agent/Core/Hooks/HookRegistry.cs`

**改动内容**: 在现有文件末尾新增 MCP 区域

**新增 Hook 点**:

```csharp
#region MCP 生命周期

/// <summary>MCP 初始化前 - 阻塞策略</summary>
public static readonly HookSpec McpBeforeInitialize = new(HookPolicy.Blocking, "mcp.before_initialize");

/// <summary>MCP Server 连接前 - 阻塞策略（可拦截）</summary>
public static readonly HookSpec McpBeforeConnect = new(HookPolicy.Blocking, "mcp.before_connect");

/// <summary>MCP Server 连接后 - 非阻塞策略</summary>
public static readonly HookSpec McpAfterConnect = new(HookPolicy.FireAndForget, "mcp.after_connect");

/// <summary>MCP Server 断开连接 - 非阻塞策略</summary>
public static readonly HookSpec McpDisconnected = new(HookPolicy.FireAndForget, "mcp.disconnected");

/// <summary>MCP Server 状态变更 - 非阻塞策略</summary>
public static readonly HookSpec McpStatusChanged = new(HookPolicy.FireAndForget, "mcp.status_changed");

/// <summary>MCP Server 错误 - 非阻塞策略</summary>
public static readonly HookSpec McpOnError = new(HookPolicy.FireAndForget, "mcp.on_error");

/// <summary>MCP 工具注册前 - 阻塞策略</summary>
public static readonly HookSpec McpToolBeforeRegister = new(HookPolicy.Blocking, "mcp.tool.before_register");

/// <summary>MCP 工具注册后 - 非阻塞策略</summary>
public static readonly HookSpec McpToolAfterRegister = new(HookPolicy.FireAndForget, "mcp.tool.after_register");

/// <summary>MCP 工具注销 - 非阻塞策略</summary>
public static readonly HookSpec McpToolUnregistered = new(HookPolicy.FireAndForget, "mcp.tool.unregistered");

/// <summary>MCP 重连前 - 阻塞策略</summary>
public static readonly HookSpec McpBeforeReconnect = new(HookPolicy.Blocking, "mcp.before_reconnect");

/// <summary>MCP 重连后 - 非阻塞策略</summary>
public static readonly HookSpec McpAfterReconnect = new(HookPolicy.FireAndForget, "mcp.after_reconnect");

/// <summary>MCP 配置更新前 - 阻塞策略</summary>
public static readonly HookSpec McpBeforeConfigUpdate = new(HookPolicy.Blocking, "mcp.before_config_update");

/// <summary>MCP 关闭 - 非阻塞策略</summary>
public static readonly HookSpec McpShutdown = new(HookPolicy.FireAndForget, "mcp.shutdown");

#endregion
```

---

## Phase 2: 管理层实现（部分并行）

**预计时间**: 3小时  
**依赖**: Phase 1 完成  
**可并行**: 4 个任务可同时执行（Task 2.1-2.4）

---

### Task 2.1: McpToolRegistry 工具注册管理

**路径**: `src/Seeing.Agent/MCP/Management/McpToolRegistry.cs`

**职责**: 管理 MCP 工具的注册和注销，与 ToolInvoker 集成

**关键实现**:

```csharp
internal sealed class McpToolRegistry
{
    private readonly ToolInvoker _toolInvoker;
    private readonly IHookManager _hookManager;
    private readonly ILogger _logger;
    
    private readonly ConcurrentDictionary<string, HashSet<string>> _serverTools = new();
    private readonly ConcurrentDictionary<string, McpTool> _mcpTools = new();
    
    public async Task RegisterToolAsync(string serverName, McpToolInfo toolInfo, CancellationToken ct)
    {
        // Hook: mcp.tool.before_register
        var beforeResult = await _hookManager.TriggerBlockingAsync(
            HookRegistry.McpToolBeforeRegister, string.Empty,
            new Dictionary<string, object?> { ["serverName"] = serverName, ["toolId"] = toolId },
            cancellationToken: ct);
        
        if (!beforeResult.Continue) return;
        
        // 创建 McpTool 并注册
        var mcpTool = new McpTool(serverName, toolInfo.Name, ...);
        await _toolInvoker.RegisterToolAsync(mcpTool, ct);
        
        // Hook: mcp.tool.after_register
        _hookManager.TriggerFireAndForget(HookRegistry.McpToolAfterRegister, ...);
    }
    
    public async Task UnregisterAllToolsAsync(string serverName)
    {
        // 遍历注销所有工具
        foreach (var toolId in _serverTools[serverName])
        {
            _toolInvoker.UnregisterTool(toolId);
            _hookManager.TriggerFireAndForget(HookRegistry.McpToolUnregistered, ...);
        }
    }
}
```

---

### Task 2.2: McpConnectionCoordinator 连接协调器

**路径**: `src/Seeing.Agent/MCP/Management/McpConnectionCoordinator.cs`

**职责**: 单个 Server 的连接生命周期管理，并发控制

**关键实现**:

```csharp
internal sealed class McpConnectionCoordinator : IDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);  // 并发锁
    
    public async Task<McpOperationResult> ConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            // Hook: mcp.before_connect
            var beforeResult = await _hookManager.TriggerBlockingAsync(HookRegistry.McpBeforeConnect, ...);
            if (!beforeResult.Continue) return McpOperationResult.Failed(...);
            
            // 执行连接
            _client = _factoryRegistry.Create(config, ...);
            await _client.ConnectAsync(ct);
            
            // 注册工具
            var tools = await _client.ListToolsAsync(ct);
            foreach (var tool in tools)
                await _toolRegistry.RegisterToolAsync(serverName, tool, ct);
            
            // 更新状态
            UpdateState(McpConnectionState.Connected);
            
            // Hook: mcp.after_connect
            _hookManager.TriggerFireAndForget(HookRegistry.McpAfterConnect, ...);
            
            return McpOperationResult.Succeeded(...);
        }
        finally { _connectLock.Release(); }
    }
    
    public Task ConnectInBackgroundAsync(McpServerConfig config, CancellationToken ct)
    {
        // 后台启动连接（不阻塞）
        return Task.Run(async () => await ConnectAsync(config, ct), ct);
    }
    
    public async Task<McpOperationResult> DisconnectAsync() { ... }
    public async Task<McpOperationResult> ReconnectAsync(CancellationToken ct) { ... }
    public McpOperationResult Pause() { ... }
    public McpOperationResult Resume() { ... }
}
```

---

### Task 2.3: McpBackgroundReconnector 后台重连器

**路径**: `src/Seeing.Agent/MCP/Management/McpBackgroundReconnector.cs`

**职责**: 定时检查 Error 状态 Server 并自动重连

**关键实现**:

```csharp
internal sealed class McpBackgroundReconnector
{
    private Timer? _timer;
    
    public void Start(CancellationToken cancellationToken)
    {
        _timer = new Timer(CheckAndReconnect, null,
            _policy.BackgroundCheckInterval, _policy.BackgroundCheckInterval);
    }
    
    private async void CheckAndReconnect(object? state)
    {
        var errorServers = _manager.GetAllStatus()
            .Where(s => s.Value.State == McpConnectionState.Error && s.Value.CanReconnect)
            .OrderBy(s => s.Value.Priority);  // 按优先级排序
        
        foreach (var kvp in errorServers)
        {
            var status = kvp.Value;
            var nextInterval = status.ActivePolicy.CalculateInterval(status.ReconnectAttempts);
            
            // 检查是否到达下次重连时间
            if (DateTime.UtcNow - status.LastReconnectAt < nextInterval) continue;
            
            await _manager.ReconnectServerAsync(kvp.Key);
        }
    }
    
    public void Stop() { _timer?.Dispose(); }
}
```

---

### Task 2.4: McpProcessMonitor 进程监控

**路径**: `src/Seeing.Agent/MCP/Management/McpProcessMonitor.cs`

**职责**: 监控 stdio 进程退出，自动标记 Error 状态

**关键实现**:

```csharp
internal sealed class McpProcessMonitor
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    
    public void Watch(string serverName, StdioMcpClientWrapper wrapper)
    {
        var process = wrapper.GetProcess();
        if (process == null) return;
        
        _processes[serverName] = process;
        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => OnProcessExited(serverName);
    }
    
    private void OnProcessExited(string serverName)
    {
        var error = McpErrorInfo.ProcessCrashed(_configs[serverName].Command);
        
        var newStatus = McpServerStatusBuilder.From(_statuses[serverName])
            .WithState(McpConnectionState.Error)
            .WithError(error)
            .Build();
        
        _manager.UpdateState(serverName, newStatus);
    }
    
    public void Unwatch(string serverName) { ... }
    public void Stop() { ... }
}
```

---

## Phase 3: 核心集成（串行执行）

**预计时间**: 3小时  
**依赖**: Phase 2 完成  

---

### Task 3.1: 重构 McpClientManager 核心管理器

**路径**: `src/Seeing.Agent/MCP/McpClientManager.cs`

**改动**: 完全重构现有文件，实现 IMcpManager 接口

**关键结构**:

```csharp
public sealed class McpClientManager : IMcpManager
{
    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, McpConnectionCoordinator> _coordinators = new();
    private readonly object _stateLock = new();
    
    public event EventHandler<McpStatusChangedEventArgs>? StatusChanged;
    
    // IMcpStatusProvider 实现
    public IReadOnlyDictionary<string, McpServerStatus> GetAllStatus()
    {
        lock (_stateLock)
        {
            return _statuses.ToDictionary(k => k.Key, v => v.Value.Clone());
        }
    }
    
    // IMcpController 实现
    public async Task<McpOperationResult> ConnectServerAsync(string serverName, CancellationToken ct)
    {
        var coordinator = GetOrCreateCoordinator(serverName);
        return await coordinator.ConnectAsync(_configs[serverName], ct);
    }
    
    // IMcpConfigManager 实现
    public async Task<McpOperationResult> AddServerAsync(McpServerConfig config, bool autoConnect, CancellationToken ct)
    {
        // 验证配置
        var validation = ValidateConfig(config);
        if (!validation.IsValid) return McpOperationResult.Failed(...);
        
        // 添加到字典
        _configs[config.Name] = config;
        _statuses[config.Name] = new McpServerStatusBuilder { Name = config.Name, ... }.Build();
        
        // 后台启动连接
        if (autoConnect) 
            _ = GetOrCreateCoordinator(config.Name).ConnectInBackgroundAsync(config, ct);
        
        return McpOperationResult.Succeeded(...);
    }
    
    // 非阻塞初始化
    public async Task InitializeAsync(IEnumerable<McpServerConfig> configs, CancellationToken ct)
    {
        // Hook: mcp.before_initialize
        var beforeResult = await _hookManager.TriggerBlockingAsync(HookRegistry.McpBeforeInitialize, ...);
        
        foreach (var config in configs)
        {
            _configs[config.Name] = config;
            _statuses[config.Name] = new McpServerStatusBuilder { ... }.Build();
            
            // 后台启动连接（不阻塞）
            _ = GetOrCreateCoordinator(config.Name).ConnectInBackgroundAsync(config, ct);
        }
        
        _reconnector.Start(_shutdownCts.Token);
    }
    
    internal void UpdateState(string serverName, McpServerStatus newStatus)
    {
        lock (_stateLock)
        {
            var previous = _statuses.TryGetValue(serverName, out var prev) ? prev : null;
            _statuses[serverName] = newStatus;
            OnStatusChanged(serverName, newStatus.State, previous);
        }
    }
    
    private void OnStatusChanged(string serverName, McpConnectionState newState, McpServerStatus? previous)
    {
        StatusChanged?.Invoke(this, new McpStatusChangedEventArgs(...));
        _hookManager.TriggerFireAndForget(HookRegistry.McpStatusChanged, ...);
    }
}
```

---

### Task 3.2: 改造 ComponentManager McpLoader

**路径**: `src/Seeing.Agent/Core/ComponentManager.cs`

**改动**: McpLoader 类改为非阻塞初始化

**改动内容**:

```csharp
// 原代码（阻塞）
internal class McpLoader : IComponentLoader
{
    public async Task<ComponentLoadResult> LoadAsync(...)
    {
        foreach (var config in configs)
        {
            await mcpManager.ConnectAsync(config, cancellationToken);  // 阻塞
        }
        // 注册工具
        foreach (var tool in mcpManager.GetToolsAsITools())
            toolInvoker.RegisterTool(tool);
    }
}

// 改为（非阻塞）
internal class McpLoader : IComponentLoader
{
    public async Task<ComponentLoadResult> LoadAsync(...)
    {
        var configs = McpConfigLoader.LoadDefault(workspaceRoot, logger);
        
        // 非阻塞初始化（后台连接）
        await mcpManager.InitializeAsync(configs, cancellationToken);
        
        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = configs.Count,
            Details = configs.Select(c => c.Name).ToList()
        };
    }
}
```

---

### Task 3.3: 扩展 McpServerConfig 配置模型

**路径**: `src/Seeing.Agent/MCP/McpTool.cs`

**改动**: 在 McpServerConfig 类中新增字段

**新增字段**:

```csharp
public class McpServerConfig
{
    // 现有字段保留...
    
    // 新增：Server 优先级
    [JsonPropertyName("priority")]
    public McpServerPriority Priority { get; set; } = McpServerPriority.Normal;
    
    // 新增：Server 级重连策略（覆盖全局）
    [JsonPropertyName("reconnectionPolicy")]
    public McpReconnectionPolicy? ReconnectionPolicy { get; set; }
    
    // 新增：是否自动启动
    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; } = true;
}
```

---

### Task 3.4: 扩展 ServiceCollectionExtensions DI 注册

**路径**: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

**改动**: 新增 MCP 相关服务注册

**新增内容**:

```csharp
public static IServiceCollection AddSeeingAgent(this IServiceCollection services, IConfiguration configuration)
{
    // 现有注册保留...
    
    // 新增：MCP 工厂注册
    services.AddSingleton<McpWrapperFactoryRegistry>(sp =>
    {
        var registry = new McpWrapperFactoryRegistry();
        registry.Register(new StdioWrapperFactory());
        registry.Register(new HttpWrapperFactory());
        return registry;
    });
    
    // 新增：全局策略配置
    services.AddSingleton<McpGlobalPolicy>(sp =>
    {
        var config = configuration.GetSection("SeeingAgent:Mcp");
        return new McpGlobalPolicy
        {
            ConnectionTimeout = TimeSpan.FromSeconds(config.GetValue<int>("ConnectionTimeout", 30)),
            BackgroundCheckInterval = TimeSpan.FromSeconds(config.GetValue<int>("BackgroundCheckInterval", 10)),
            AutoStartOnAdd = config.GetValue<bool>("AutoStartOnAdd", true)
        };
    });
    
    // 改造：McpClientManager 注册（替换原注册）
    services.AddSingleton<IMcpManager, McpClientManager>();
    services.AddSingleton<McpClientManager>(sp => (McpClientManager)sp.GetRequiredService<IMcpManager>());
    
    return services;
}
```

---

## Phase 4: WebUI 集成（串行执行）

**预计时间**: 2小时  
**依赖**: Phase 3 完成

---

### Task 4.1: 创建 McpStateService 状态服务

**路径**: `samples/Seeing.Agent.WebUI/Services/McpStateService.cs`

**职责**: Blazor 状态同步，线程安全事件订阅

**关键实现**:

```csharp
public sealed class McpStateService : IDisposable
{
    private readonly IMcpManager _manager;
    private readonly ConcurrentDictionary<string, McpServerStatus> _cache = new();
    private Timer? _refreshTimer;
    
    public event EventHandler? StateChanged;
    
    public McpStateService(IMcpManager manager)
    {
        _manager = manager;
        _manager.StatusChanged += OnManagerStatusChanged;
        
        // 定时刷新（1秒）
        _refreshTimer = new Timer(RefreshCache, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    
    private void RefreshCache(object? state)
    {
        var allStatus = _manager.GetAllStatus();
        foreach (var kvp in allStatus)
            _cache[kvp.Key] = kvp.Value;
        
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    
    public IReadOnlyDictionary<string, McpServerStatus> GetCachedStatus() => _cache;
    
    public void Dispose()
    {
        _manager.StatusChanged -= OnManagerStatusChanged;  // 防止内存泄漏
        _refreshTimer?.Dispose();
    }
}
```

---

### Task 4.2: 创建 McpStatusPanel.razor 状态面板

**路径**: `samples/Seeing.Agent.WebUI/Components/McpStatusPanel.razor`

**功能**: MCP 状态列表展示、操作按钮（暂停/恢复/重连/移除）

**关键实现**: 参见设计方案中的完整 Razor 代码

---

## Phase 5: 验证（串行执行）

**预计时间**: 1小时

---

### Task 5.1: 构建验证

```bash
# 构建主库
dotnet build src/Seeing.Agent

# 构建 WebUI
dotnet build samples/Seeing.Agent.WebUI

# 运行测试
dotnet test tests/Seeing.Agent.Tests
```

---

### Task 5.2: 功能验证

1. 启动 WebUI，验证 MCP 非阻塞初始化
2. 检查 MCP 状态面板是否正确显示
3. 测试暂停/恢复/重连操作
4. 模拟 MCP 断线，验证自动重连
5. 模拟 stdio 进程崩溃，验证状态标记

---

## 执行顺序图

```
Phase 1 (并行)
├── Task 1.1 ─┬─ Task 1.2 ─┬─ Task 1.3 ─┬─ Task 1.4 ─┬─ Task 1.5 ─┬─ Task 1.6
│             │            │            │            │            │
└─────────────┴────────────┴────────────┴────────────┴────────────┘
                            ↓ (依赖 Phase 1)
Phase 2 (并行)
├── Task 2.1 ─┬─ Task 2.2 ─┬─ Task 2.3 ─┬─ Task 2.4
│             │            │            │
└─────────────┴────────────┴────────────┘
                            ↓ (依赖 Phase 2)
Phase 3 (串行)
Task 3.1 → Task 3.2 → Task 3.3 → Task 3.4
                            ↓
Phase 4 (串行)
Task 4.1 → Task 4.2
                            ↓
Phase 5 (串行)
Task 5.1 → Task 5.2
```

---

## 文件创建清单汇总

| 阶段 | 新增文件数 | 改动文件数 |
|------|-----------|-----------|
| Phase 1 | 26 | 1 |
| Phase 2 | 4 | 0 |
| Phase 3 | 0 | 4 |
| Phase 4 | 2 | 1 |
| Phase 5 | 0 | 0 |
| **总计** | **32** | **6** |

---

## 子 Agent 执行指令模板

每个 Task 可分配给独立 Agent，执行时请提供：

1. **文件路径**: 明确的创建/改动路径
2. **依赖文件**: 需要先读取的现有文件
3. **代码模板**: 关键实现的代码模板
4. **验证命令**: 完成后的验证命令

---

**准备就绪，可以开始并行执行 Phase 1**