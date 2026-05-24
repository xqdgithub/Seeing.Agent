# MCP 配置管理扩展计划（最优版）

> **版本**: v2.0  
> **状态**: ✅ 已修复所有审查问题，可直接实施  
> **创建时间**: 2025-01-21  
> **最后更新**: 2025-01-21

---

## 目标

扩展 MCP 管理功能，实现：
1. MCP 服务的热增加、删除、编辑
2. 支持基于指定 mcp.json 更新或新增 MCP 服务
3. 支持将最新配置持久化到项目级别或用户级别
4. 支持 MCP 的启用/禁用状态管理
5. 展示时支持返回 JSON 和配置路径
6. WebUI 支持 JSON 编辑和工具展示

---

## JSON 格式示例

```json
{
  "mcpServers": {
    "time": {
      "type": "streamableHttp",
      "url": "https://mcp.api-inference.modelscope.net/a6f66b92315d48/mcp",
      "headers": {
        "Authorization": "Bearer ms-de971c5b-9982-47e8-a712-6a0e0c9dffaf"
      },
      "disabled": false
    }
  }
}
```

**注意**: `type` 字段序列化输出为驼峰式 `streamableHttp`，输入时支持 `streamable_http`、`streamablehttp`、`http` 三种格式。

---

## 执行顺序

```
Phase 1: 核心模型扩展（无依赖）
├── Task 1.1: McpConfigLevel 枚举
├── Task 1.2: McpErrorCode 新错误码
├── Task 1.3: McpOperationResult 扩展
└── Task 1.4: McpServerConfig 扩展
            ↓
Phase 2: 接口定义
├── Task 2.1: IMcpConfigManager 完整接口
└── Task 2.2: IMcpConfigPersistence 接口
            ↓
Phase 3: 持久化实现
└── Task 3.1: McpConfigPersistence 完整实现
            ↓
Phase 4: McpClientManager 实现
├── Task 4.1: 构造函数依赖注入
├── Task 4.2: EnableServerAsync/DisableServerAsync
├── Task 4.3: ImportFromJsonAsync
├── Task 4.4: SaveConfigAsync
└── Task 4.5: JSON 输出方法
            ↓
Phase 5: DI 注册
└── Task 5.1: 注册新服务
            ↓
Phase 6: WebUI 组件
├── Task 6.1: McpToolList 组件
├── Task 6.2: McpJsonEditor 组件
├── Task 6.3: McpStateService 扩展
├── Task 6.4: McpPage 重构
└── Task 6.5: 样式文件
            ↓
Phase 7: 测试验证
└── Task 7.1: 功能测试
```

---

## Phase 1: 核心模型扩展

### Task 1.1: 创建 McpConfigLevel 枚举

**文件**: `src/Seeing.Agent/MCP/Core/McpConfigLevel.cs` (新建)

```csharp
namespace Seeing.Agent.MCP.Core;

/// <summary>
/// 配置保存级别
/// </summary>
public enum McpConfigLevel
{
    /// <summary>项目级别（./.seeing/mcp.json）</summary>
    Project = 0,
    
    /// <summary>用户级别（~/.seeing/mcp.json）</summary>
    User = 1
}
```

---

### Task 1.2: 扩展 McpErrorCode

**文件**: `src/Seeing.Agent/MCP/Core/McpErrorCode.cs` (修改)

**改动**: 在现有枚举末尾添加

```csharp
// 在现有枚举值后添加：

/// <summary>配置持久化失败</summary>
PersistenceError,

/// <summary>JSON 解析失败</summary>
JsonParseError,

/// <summary>配置导入失败</summary>
ImportError
```

---

### Task 1.3: 扩展 McpOperationResult

**文件**: `src/Seeing.Agent/MCP/Core/McpOperationResult.cs` (修改)

**改动**: 添加 `Details` 属性和相关方法

```csharp
public sealed class McpOperationResult
{
    public bool Success { get; init; }
    public string ServerName { get; init; }
    public McpOperationType OperationType { get; init; }
    public McpConnectionState? Status { get; init; }
    public McpErrorInfo? Error { get; init; }
    public TimeSpan Duration { get; init; }
    
    /// <summary>操作详情（可选）</summary>
    public string? Details { get; init; }

    private McpOperationResult(
        bool success,
        string serverName,
        McpOperationType operationType,
        McpConnectionState? status = null,
        McpErrorInfo? error = null,
        TimeSpan? duration = null,
        string? details = null)
    {
        Success = success;
        ServerName = serverName;
        OperationType = operationType;
        Status = status;
        Error = error;
        Duration = duration ?? TimeSpan.Zero;
        Details = details;
    }

    public static McpOperationResult Succeeded(
        string serverName,
        McpOperationType operationType,
        McpConnectionState? status = null,
        TimeSpan? duration = null)
        => new(true, serverName, operationType, status, null, duration);

    public static McpOperationResult Failed(
        string serverName,
        McpOperationType operationType,
        McpErrorInfo error,
        McpConnectionState? status = null,
        TimeSpan? duration = null)
        => new(false, serverName, operationType, status, error, duration);

    public static McpOperationResult NoChange(
        string serverName,
        McpOperationType operationType,
        McpConnectionState status,
        TimeSpan? duration = null)
        => new(true, serverName, operationType, status, null, duration);
    
    /// <summary>创建带详情的成功结果</summary>
    public static McpOperationResult SucceededWithDetails(
        string serverName,
        McpOperationType operationType,
        string details,
        McpConnectionState? status = null,
        TimeSpan? duration = null)
        => new(true, serverName, operationType, status, null, duration, details);
    
    /// <summary>返回带详情的新实例</summary>
    public McpOperationResult WithDetails(string details)
        => new(Success, ServerName, OperationType, Status, Error, Duration, details);
}
```

---

### Task 1.4: 扩展 McpServerConfig

**文件**: `src/Seeing.Agent/MCP/McpTool.cs` (修改)

**改动**: 在 `McpServerConfig` 类中添加运行时字段

```csharp
// 在 McpServerConfig 类中添加：

/// <summary>配置来源级别（运行时设置，不序列化）</summary>
[JsonIgnore]
public McpConfigLevel? ConfigLevel { get; set; }
```

**说明**: 复用现有 `Disabled` 字段（默认 `false`），不添加 `Enabled` 字段。

---

## Phase 2: 接口定义

### Task 2.1: 重写 IMcpConfigManager 接口

**文件**: `src/Seeing.Agent/MCP/Core/IMcpConfigManager.cs` (完全重写)

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Seeing.Agent.MCP.Core;

public interface IMcpConfigManager
{
    #region 服务器管理
    
    /// <summary>添加服务器配置</summary>
    Task<McpOperationResult> AddServerAsync(string name, McpServerConfig config, CancellationToken cancellationToken = default);
    
    /// <summary>移除服务器配置</summary>
    Task<McpOperationResult> RemoveServerAsync(string name, CancellationToken cancellationToken = default);
    
    /// <summary>更新服务器配置</summary>
    Task<McpOperationResult> UpdateConfigAsync(string name, McpServerConfig config, CancellationToken cancellationToken = default);
    
    /// <summary>启用服务器</summary>
    Task<McpOperationResult> EnableServerAsync(string name, CancellationToken cancellationToken = default);
    
    /// <summary>禁用服务器</summary>
    Task<McpOperationResult> DisableServerAsync(string name, CancellationToken cancellationToken = default);
    
    /// <summary>从 JSON 批量导入服务器配置</summary>
    /// <param name="mcpServersJson">mcpServers 对象的 JSON（不包含外层包装）</param>
    /// <param name="level">保存到的配置级别</param>
    /// <param name="merge">true=合并，false=替换</param>
    Task<McpOperationResult> ImportFromJsonAsync(JsonElement mcpServersJson, McpConfigLevel level, bool merge = true, CancellationToken cancellationToken = default);
    
    /// <summary>重载所有配置</summary>
    Task<int> ReloadAllAsync(CancellationToken cancellationToken = default);
    
    #endregion
    
    #region 配置验证与查询
    
    /// <summary>验证配置</summary>
    (bool Valid, string? Error) ValidateConfig(string name, McpServerConfig config);
    
    /// <summary>获取服务器配置</summary>
    McpServerConfig? GetConfig(string name);
    
    /// <summary>获取所有服务器配置</summary>
    IReadOnlyDictionary<string, McpServerConfig> GetAllConfigs();
    
    /// <summary>获取配置来源级别</summary>
    McpConfigLevel? GetConfigLevel(string name);
    
    #endregion
    
    #region 持久化
    
    /// <summary>保存配置到指定级别</summary>
    Task<McpOperationResult> SaveConfigAsync(McpConfigLevel level, CancellationToken cancellationToken = default);
    
    /// <summary>获取配置文件路径</summary>
    string GetConfigFilePath(McpConfigLevel level);
    
    /// <summary>获取所有配置的 JSON 表示</summary>
    JsonElement GetConfigsAsJson();
    
    /// <summary>获取单个服务器配置的 JSON 表示</summary>
    JsonElement? GetServerConfigAsJson(string name);
    
    #endregion
}
```

---

### Task 2.2: 创建 IMcpConfigPersistence 接口

**文件**: `src/Seeing.Agent/MCP/Configuration/IMcpConfigPersistence.cs` (新建)

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.MCP.Core;

namespace Seeing.Agent.MCP.Configuration;

/// <summary>
/// MCP 配置持久化接口
/// </summary>
public interface IMcpConfigPersistence
{
    /// <summary>加载指定级别的配置</summary>
    Task<IReadOnlyDictionary<string, McpServerConfig>> LoadAsync(McpConfigLevel level, CancellationToken cancellationToken = default);
    
    /// <summary>保存配置到指定级别</summary>
    Task SaveAsync(McpConfigLevel level, IReadOnlyDictionary<string, McpServerConfig> configs, CancellationToken cancellationToken = default);
    
    /// <summary>解析单个服务器配置</summary>
    McpServerConfig? ParseServerConfig(string name, JsonElement element);
    
    /// <summary>序列化单个服务器配置</summary>
    Dictionary<string, object> SerializeServerConfig(McpServerConfig config);
    
    /// <summary>获取配置文件路径</summary>
    string GetConfigPath(McpConfigLevel level);
    
    /// <summary>检查配置文件是否存在</summary>
    bool ConfigExists(McpConfigLevel level);
}
```

---

## Phase 3: 持久化实现

### Task 3.1: 创建 McpConfigPersistence 完整实现

**文件**: `src/Seeing.Agent/MCP/Configuration/McpConfigPersistence.cs` (新建)

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.MCP.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.MCP.Configuration;

public class McpConfigPersistence : IMcpConfigPersistence
{
    private readonly ILogger<McpConfigPersistence> _logger;
    private readonly string _workspaceRoot;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public McpConfigPersistence(ILogger<McpConfigPersistence> logger, string? workspaceRoot = null)
    {
        _logger = logger;
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }
    
    public string GetConfigPath(McpConfigLevel level)
    {
        return level switch
        {
            McpConfigLevel.User => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "mcp.json"),
            McpConfigLevel.Project => Path.Combine(_workspaceRoot, ".seeing", "mcp.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };
    }
    
    public bool ConfigExists(McpConfigLevel level) => File.Exists(GetConfigPath(level));
    
    public async Task<IReadOnlyDictionary<string, McpServerConfig>> LoadAsync(
        McpConfigLevel level, CancellationToken cancellationToken = default)
    {
        var path = GetConfigPath(level);
        if (!File.Exists(path))
            return new Dictionary<string, McpServerConfig>();
        
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers))
                return new Dictionary<string, McpServerConfig>();
            
            var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in servers.EnumerateObject())
            {
                var config = ParseServerConfig(prop.Name, prop.Value);
                if (config != null && config.IsValid())
                {
                    config.ConfigLevel = level;
                    configs[prop.Name] = config;
                }
            }
            
            return configs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 MCP 配置失败: {Path}", path);
            return new Dictionary<string, McpServerConfig>();
        }
    }
    
    public async Task SaveAsync(
        McpConfigLevel level,
        IReadOnlyDictionary<string, McpServerConfig> configs,
        CancellationToken cancellationToken = default)
    {
        var path = GetConfigPath(level);
        var directory = Path.GetDirectoryName(path)!;
        
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        
        var output = new Dictionary<string, object>
        {
            ["mcpServers"] = configs.ToDictionary(
                kvp => kvp.Key,
                kvp => SerializeServerConfig(kvp.Value))
        };
        
        var json = JsonSerializer.Serialize(output, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        
        _logger.LogInformation("已保存 {Count} 个 MCP 配置到: {Path}", configs.Count, path);
    }
    
    public McpServerConfig? ParseServerConfig(string name, JsonElement element)
    {
        try
        {
            var transportType = element.TryGetProperty("type", out var ttProp)
                ? ParseTransportType(ttProp.GetString())
                : McpTransportType.Stdio;
            
            var config = new McpServerConfig
            {
                Name = name,
                TransportType = transportType
            };
            
            // stdio 配置
            if (element.TryGetProperty("command", out var cmdProp))
                config.Command = cmdProp.GetString();
            
            if (element.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
                config.Args = argsProp.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
            
            if (element.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
                config.Env = envProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            
            if (element.TryGetProperty("workingDirectory", out var wdProp))
                config.WorkingDirectory = wdProp.GetString();
            
            // HTTP 配置
            if (element.TryGetProperty("url", out var urlProp))
            {
                var urlStr = urlProp.GetString();
                if (!string.IsNullOrEmpty(urlStr) && Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
                    config.Url = uri;
            }
            
            if (element.TryGetProperty("headers", out var hdrProp) && hdrProp.ValueKind == JsonValueKind.Object)
                config.Headers = hdrProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            
            // 禁用状态
            if (element.TryGetProperty("disabled", out var disabledProp))
                config.Disabled = disabledProp.GetBoolean();
            
            // 其他配置字段...
            if (element.TryGetProperty("description", out var descProp))
                config.Description = descProp.GetString();
            
            if (element.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
                config.Tags = tagsProp.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
            
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析 MCP 配置失败: {Name}", name);
            return null;
        }
    }
    
    public Dictionary<string, object> SerializeServerConfig(McpServerConfig config)
    {
        var result = new Dictionary<string, object>
        {
            ["type"] = config.TransportType switch
            {
                McpTransportType.Stdio => "stdio",
                McpTransportType.StreamableHttp => "streamableHttp",
                McpTransportType.Sse => "sse",
                _ => "stdio"
            }
        };
        
        // stdio 配置
        if (!string.IsNullOrEmpty(config.Command))
            result["command"] = config.Command;
        
        if (config.Args != null && config.Args.Count > 0)
            result["args"] = config.Args;
        
        if (config.Env != null && config.Env.Count > 0)
            result["env"] = config.Env;
        
        if (!string.IsNullOrEmpty(config.WorkingDirectory))
            result["workingDirectory"] = config.WorkingDirectory;
        
        // HTTP 配置
        if (config.Url != null)
            result["url"] = config.Url.ToString();
        
        if (config.Headers != null && config.Headers.Count > 0)
            result["headers"] = config.Headers;
        
        // 禁用状态
        if (config.Disabled)
            result["disabled"] = true;
        
        // 其他字段
        if (!string.IsNullOrEmpty(config.Description))
            result["description"] = config.Description;
        
        if (config.Tags != null && config.Tags.Count > 0)
            result["tags"] = config.Tags;
        
        return result;
    }
    
    private static McpTransportType ParseTransportType(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "stdio" => McpTransportType.Stdio,
            "streamable_http" or "streamablehttp" or "http" => McpTransportType.StreamableHttp,
            "sse" => McpTransportType.Sse,
            _ => McpTransportType.Stdio
        };
    }
}
```

---

## Phase 4: McpClientManager 实现

### Task 4.1: 构造函数依赖注入

**文件**: `src/Seeing.Agent/MCP/McpClientManager.cs` (修改)

**改动 1**: 添加字段

```csharp
// 在字段声明区域添加：
private readonly IMcpConfigPersistence _configPersistence;
```

**改动 2**: 修改主构造函数

```csharp
public McpClientManager(
    ILogger<McpClientManager> logger,
    ILoggerFactory loggerFactory,
    IHookManager hookManager,
    ToolInvoker toolInvoker,
    McpWrapperFactoryRegistry factoryRegistry,
    McpGlobalPolicy globalPolicy,
    IMcpConfigPersistence configPersistence,  // 新增
    IHttpClientFactory? httpClientFactory = null)
{
    _logger = logger;
    _loggerFactory = loggerFactory;
    _hookManager = hookManager;
    _toolInvoker = toolInvoker;
    _factoryRegistry = factoryRegistry;
    _globalPolicy = globalPolicy;
    _configPersistence = configPersistence;  // 新增
    _httpClientFactory = httpClientFactory;

    _toolRegistry = new McpToolRegistry(_toolInvoker, _hookManager, logger);
    _processMonitor = new McpProcessMonitor(logger);
    _reconnector = new McpBackgroundReconnector(
        logger,
        _globalPolicy,
        async name => { /* ... */ },
        GetAllStatus);
}
```

**改动 3**: 修改兼容性构造函数

```csharp
public McpClientManager(
    ILogger<McpClientManager> logger,
    ILoggerFactory loggerFactory,
    IHttpClientFactory? httpClientFactory = null)
    : this(
        logger,
        loggerFactory,
        GetOrCreateDefaultHookManager(loggerFactory),
        GetOrCreateDefaultToolInvoker(loggerFactory),
        CreateDefaultFactoryRegistry(loggerFactory),
        new McpGlobalPolicy(),
        CreateDefaultConfigPersistence(loggerFactory),  // 新增
        httpClientFactory)
{
}

// 添加新方法：
private static McpConfigPersistence CreateDefaultConfigPersistence(ILoggerFactory loggerFactory)
    => new(loggerFactory.CreateLogger<McpConfigPersistence>());
```

---

### Task 4.2: 实现 EnableServerAsync/DisableServerAsync

**文件**: `src/Seeing.Agent/MCP/McpClientManager.cs` (在 IMcpConfigManager 区域添加)

```csharp
public async Task<McpOperationResult> EnableServerAsync(string name, CancellationToken cancellationToken = default)
{
    try
    {
        lock (_stateLock)
        {
            var config = GetConfig(name);
            if (config == null)
                return McpOperationResult.Failed(name, McpOperationType.UpdateConfig, McpErrorInfo.ConfigMissing(name));
            
            if (!config.Disabled)
                return McpOperationResult.NoChange(name, McpOperationType.UpdateConfig, GetStatus(name)?.State);
            
            config.Disabled = false;
        }
        
        var status = GetStatus(name);
        if (status?.State == McpConnectionState.Paused)
        {
            return await ResumeServer(name);
        }
        
        var coordinator = GetOrCreateCoordinator(name);
        var configToConnect = GetConfig(name);
        if (configToConnect != null)
            _ = coordinator.ConnectInBackgroundAsync(configToConnect, cancellationToken);
        
        return McpOperationResult.Succeeded(name, McpOperationType.UpdateConfig, McpConnectionState.Connecting);
    }
    catch (Exception ex)
    {
        return McpOperationResult.Failed(name, McpOperationType.UpdateConfig,
            McpErrorInfo.FromException(McpErrorCode.ToolExecutionError, ex, name));
    }
}

public async Task<McpOperationResult> DisableServerAsync(string name, CancellationToken cancellationToken = default)
{
    try
    {
        lock (_stateLock)
        {
            var config = GetConfig(name);
            if (config == null)
                return McpOperationResult.Failed(name, McpOperationType.UpdateConfig, McpErrorInfo.ConfigMissing(name));
            
            if (config.Disabled)
                return McpOperationResult.NoChange(name, McpOperationType.UpdateConfig, GetStatus(name)?.State);
            
            config.Disabled = true;
        }
        
        var status = GetStatus(name);
        if (status?.IsAvailable == true)
        {
            await DisconnectServerAsync(name, cancellationToken);
        }
        
        return PauseServer(name);
    }
    catch (Exception ex)
    {
        return McpOperationResult.Failed(name, McpOperationType.UpdateConfig,
            McpErrorInfo.FromException(McpErrorCode.ToolExecutionError, ex, name));
    }
}
```

---

### Task 4.3: 实现 ImportFromJsonAsync

```csharp
public async Task<McpOperationResult> ImportFromJsonAsync(
    JsonElement mcpServersJson,
    McpConfigLevel level,
    bool merge = true,
    CancellationToken cancellationToken = default)
{
    if (mcpServersJson.ValueKind != JsonValueKind.Object)
        return McpOperationResult.Failed("", McpOperationType.Add,
            McpErrorInfo.ConfigInvalid("", "JSON 必须是对象类型"));
    
    var imported = new Dictionary<string, McpServerConfig>();
    var errors = new List<string>();
    
    foreach (var prop in mcpServersJson.EnumerateObject())
    {
        var config = _configPersistence.ParseServerConfig(prop.Name, prop.Value);
        if (config == null)
        {
            errors.Add($"无法解析: {prop.Name}");
            continue;
        }
        
        var validation = McpConfigValidator.Validate(config);
        if (!validation.IsValid)
        {
            errors.Add($"{prop.Name}: {validation.Error?.UserMessage}");
            continue;
        }
        
        config.ConfigLevel = level;
        imported[prop.Name] = config;
    }
    
    if (imported.Count == 0)
        return McpOperationResult.Failed("", McpOperationType.Add,
            McpErrorInfo.ConfigInvalid("", $"导入失败: {string.Join("; ", errors)}"));
    
    if (merge)
    {
        foreach (var kvp in imported)
        {
            await AddServerAsync(kvp.Key, kvp.Value, cancellationToken);
        }
    }
    else
    {
        foreach (var name in _configs.Keys.ToList())
        {
            await RemoveServerAsync(name, cancellationToken);
        }
        
        foreach (var kvp in imported)
        {
            await AddServerAsync(kvp.Key, kvp.Value, cancellationToken);
        }
    }
    
    await SaveConfigAsync(level, cancellationToken);
    
    return McpOperationResult.SucceededWithDetails("", McpOperationType.Add, $"导入 {imported.Count} 个服务器");
}
```

---

### Task 4.4: 实现 SaveConfigAsync

```csharp
public async Task<McpOperationResult> SaveConfigAsync(
    McpConfigLevel level,
    CancellationToken cancellationToken = default)
{
    try
    {
        var configs = GetAllConfigs();
        await _configPersistence.SaveAsync(level, configs, cancellationToken);
        
        return McpOperationResult.Succeeded("", McpOperationType.UpdateConfig, null);
    }
    catch (Exception ex)
    {
        return McpOperationResult.Failed("", McpOperationType.UpdateConfig,
            McpErrorInfo.FromException(McpErrorCode.PersistenceError, ex, ""));
    }
}
```

---

### Task 4.5: 实现 JSON 输出方法

```csharp
public JsonElement GetConfigsAsJson()
{
    var configs = GetAllConfigs();
    var output = new Dictionary<string, object>
    {
        ["mcpServers"] = configs.ToDictionary(
            kvp => kvp.Key,
            kvp => _configPersistence.SerializeServerConfig(kvp.Value))
    };
    
    return JsonSerializer.SerializeToElement(output);
}

public JsonElement? GetServerConfigAsJson(string name)
{
    var config = GetConfig(name);
    if (config == null) return null;
    
    var output = new Dictionary<string, object>
    {
        ["mcpServers"] = new Dictionary<string, object>
        {
            [name] = _configPersistence.SerializeServerConfig(config)
        }
    };
    
    return JsonSerializer.SerializeToElement(output);
}

public IReadOnlyDictionary<string, McpServerConfig> GetAllConfigs()
{
    lock (_stateLock)
    {
        return _configs.ToDictionary(k => k.Key, v => v.Value);
    }
}

public McpConfigLevel? GetConfigLevel(string name)
{
    var config = GetConfig(name);
    return config?.ConfigLevel;
}

public string GetConfigFilePath(McpConfigLevel level)
{
    return _configPersistence.GetConfigPath(level);
}
```

---

## Phase 5: DI 注册

### Task 5.1: 注册新服务

**文件**: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` (修改)

**改动**: 在 MCP 相关注册区域添加

```csharp
// 在 McpClientManager 注册之前添加：
services.AddSingleton<IMcpConfigPersistence, McpConfigPersistence>();
```

---

## Phase 6: WebUI 组件

### Task 6.1: 创建 McpToolList 组件

**文件**: `samples/Seeing.Agent.WebUI/Components/McpToolList.razor` (新建)

```razor
@using AntDesign
@using Seeing.Agent.MCP.Core

<div class="mcp-tool-list">
    <div class="tool-list-header" @onclick="ToggleExpand">
        <Icon Type="@IconType.Outline.Tool" />
        <span>可用工具 (@Tools.Count)</span>
        <Icon Type="@(_expanded ? IconType.Outline.Down : IconType.Outline.Right)" />
    </div>
    
    @if (_expanded && Tools.Any())
    {
        <div class="tool-list-content">
            @foreach (var tool in Tools)
            {
                <div class="tool-item">
                    <div class="tool-name">
                        <Icon Type="@IconType.Outline.Tool" />
                        <span>@tool.Name</span>
                    </div>
                    <div class="tool-desc">@tool.Description</div>
                </div>
            }
        </div>
    }
</div>

@code {
    [Parameter] public IReadOnlyList<McpToolInfo> Tools { get; set; } = Array.Empty<McpToolInfo>();
    private bool _expanded = false;
    
    void ToggleExpand() => _expanded = !_expanded;
}
```

---

### Task 6.2: 创建 McpJsonEditor 组件

**文件**: `samples/Seeing.Agent.WebUI/Components/McpJsonEditor.razor` (新建)

```razor
@using AntDesign
@using System.Text.Json

<div class="mcp-json-editor">
    <div class="editor-header">
        <span>JSON 编辑器</span>
        <Space>
            <SpaceItem>
                <Button Type="@ButtonType.Text" 
                        Size="@ButtonSize.Small"
                        Icon="@IconType.Outline.FormatPainter"
                        OnClick="FormatJson">格式化</Button>
            </SpaceItem>
        </Space>
    </div>
    
    <div class="editor-body">
        <TextArea @bind-Value="_jsonContent"
                  @bind-Value:event="oninput"
                  Rows="@Rows"
                  Placeholder="@Placeholder"
                  Class="@(_isValid ? "" : "json-error")" />
    </div>
    
    @if (!_isValid && !string.IsNullOrEmpty(_errorMessage))
    {
        <div class="editor-error">
            <Icon Type="@IconType.Outline.CloseCircle" />
            <span>@_errorMessage</span>
        </div>
    }
</div>

@code {
    [Parameter] public string JsonContent { get; set; } = "";
    [Parameter] public EventCallback<string> JsonContentChanged { get; set; }
    [Parameter] public int Rows { get; set; } = 15;
    [Parameter] public string Placeholder { get; set; } = "{\n  \"mcpServers\": {\n    \"name\": {\n      \"type\": \"stdio\",\n      \"command\": \"...\"\n    }\n  }\n}";
    
    private string _jsonContent = "";
    private bool _isValid = true;
    private string _errorMessage = "";
    
    protected override void OnParametersSet()
    {
        _jsonContent = JsonContent;
    }
    
    private async Task OnInput(ChangeEventArgs e)
    {
        _jsonContent = e.Value?.ToString() ?? "";
        ValidateJson();
        await JsonContentChanged.InvokeAsync(_jsonContent);
    }
    
    private void ValidateJson()
    {
        if (string.IsNullOrWhiteSpace(_jsonContent))
        {
            _isValid = true;
            _errorMessage = "";
            return;
        }
        
        try
        {
            JsonDocument.Parse(_jsonContent);
            _isValid = true;
            _errorMessage = "";
        }
        catch (JsonException ex)
        {
            _isValid = false;
            _errorMessage = $"JSON 格式错误: {ex.Message}";
        }
    }
    
    private void FormatJson()
    {
        try
        {
            var doc = JsonDocument.Parse(_jsonContent);
            var options = new JsonSerializerOptions { WriteIndented = true };
            _jsonContent = JsonSerializer.Serialize(doc.RootElement, options);
            _isValid = true;
        }
        catch
        {
            // 格式化失败
        }
    }
}
```

---

### Task 6.3: 扩展 McpStateService

**文件**: `samples/Seeing.Agent.WebUI/Services/McpStateService.cs` (修改)

```csharp
// 添加新方法：

/// <summary>获取所有 MCP 工具</summary>
public IReadOnlyList<McpToolInfo> GetAllTools()
{
    return _manager.GetTools();
}

/// <summary>获取指定 Server 的工具</summary>
public IReadOnlyList<McpToolInfo> GetServerTools(string serverName)
{
    return _manager.GetTools().Where(t => t.ServerName == serverName).ToList();
}

/// <summary>获取配置文件路径</summary>
public string GetConfigFilePath(McpConfigLevel level)
{
    return _manager.GetConfigFilePath(level);
}

/// <summary>获取所有配置的 JSON 字符串</summary>
public string GetConfigsAsJsonString()
{
    var json = _manager.GetConfigsAsJson();
    return JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>获取单个服务配置的 JSON 字符串</summary>
public string? GetServerConfigAsJsonString(string name)
{
    var json = _manager.GetServerConfigAsJson(name);
    if (json == null) return null;
    return JsonSerializer.Serialize(json.Value, new JsonSerializerOptions { WriteIndented = true });
}
```

---

### Task 6.4: 重构 McpPage 页面

**文件**: `samples/Seeing.Agent.WebUI/Pages/McpPage.razor` (修改)

**添加 using**:
```razor
@using System.Text.Json
```

**添加字段**:
```csharp
private bool _jsonEditorVisible = false;
private string _editMode = "";  // "add" or "edit"
private string _editingJson = "";
private string _editingServerName = "";
private McpConfigLevel _saveLevel = McpConfigLevel.Project;
private bool _saving = false;
```

**添加操作按钮** (在现有按钮区域):
```razor
<SpaceItem>
    <Button Size="@ButtonSize.Small" 
            Type="@ButtonType.Text"
            Icon="@IconType.Outline.Edit"
            OnClick="@(() => EditServer(status.Name))">编辑</Button>
</SpaceItem>
<SpaceItem>
    <Button Size="@ButtonSize.Small" 
            Type="@ButtonType.Text"
            Icon="@IconType.Outline.Delete"
            Danger="true"
            OnClick="@(() => DeleteServer(status.Name))">删除</Button>
</SpaceItem>
@if (status.Config?.Disabled == true)
{
    <SpaceItem>
        <Button Size="@ButtonSize.Small" 
                Type="@ButtonType.Primary"
                Icon="@IconType.Outline.PlayCircle"
                OnClick="@(() => EnableServer(status.Name))">启用</Button>
    </SpaceItem>
}
else
{
    <SpaceItem>
        <Button Size="@ButtonSize.Small"
                Icon="@IconType.Outline.Stop"
                OnClick="@(() => DisableServer(status.Name))">禁用</Button>
    </SpaceItem>
}
```

**添加工具展示**:
```razor
<div class="item-card-tools">
    <McpToolList Tools="@McpStateService.GetServerTools(status.Name)" />
</div>
```

**添加 JSON 编辑弹窗**:
```razor
<Modal @bind-Visible="_jsonEditorVisible" 
       Title="@(_editMode == "add" ? "添加 MCP 服务" : "编辑 MCP 服务")" 
       Width="800">
    <Alert Type="@AlertType.Info" Message="配置格式" 
           Description="JSON 需包含 mcpServers 对象" ShowIcon />
    
    <McpJsonEditor @bind-JsonContent="_editingJson" Rows="18" />
    
    <div class="config-path-info">
        <span>保存到：</span>
        <Select @bind-Value="_saveLevel" Style="width: 200px;">
            <SelectOption Value="@McpConfigLevel.Project">项目级</SelectOption>
            <SelectOption Value="@McpConfigLevel.User">用户级</SelectOption>
        </Select>
        <Text Type="@TextElementType.Secondary">@McpStateService.GetConfigFilePath(_saveLevel)</Text>
    </div>
    
    <div slot="footer">
        <Button OnClick="@(() => _jsonEditorVisible = false)">取消</Button>
        <Button Type="@ButtonType.Primary" OnClick="SaveJsonEdit" Loading="_saving">保存</Button>
    </div>
</Modal>
```

**添加方法**:
```csharp
void EditServer(string name)
{
    var json = McpStateService.GetServerConfigAsJsonString(name);
    if (json != null)
    {
        _editMode = "edit";
        _editingServerName = name;
        _editingJson = json;
        _jsonEditorVisible = true;
    }
}

async Task SaveJsonEdit()
{
    _saving = true;
    try
    {
        using var doc = JsonDocument.Parse(_editingJson);
        if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
        {
            var result = await McpManager.ImportFromJsonAsync(servers, _saveLevel, merge: true);
            if (result.Success)
            {
                _jsonEditorVisible = false;
                MessageService.Success("配置已保存");
                RefreshStatus();
            }
            else
            {
                MessageService.Error($"保存失败: {result.Error?.UserMessage}");
            }
        }
        else
        {
            MessageService.Error("JSON 缺少 mcpServers 字段");
        }
    }
    catch (JsonException ex)
    {
        MessageService.Error($"JSON 格式错误: {ex.Message}");
    }
    finally
    {
        _saving = false;
    }
}

async Task DeleteServer(string name)
{
    var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
    {
        Title = "确认删除",
        Content = $"确定删除 '{name}'？"
    });
    
    if (confirmed)
    {
        var result = await McpManager.RemoveServerAsync(name);
        if (result.Success)
        {
            MessageService.Success("已删除");
            RefreshStatus();
        }
        else
        {
            MessageService.Error($"删除失败: {result.Error?.UserMessage}");
        }
    }
}

async Task EnableServer(string name)
{
    var result = await McpManager.EnableServerAsync(name);
    if (result.Success)
    {
        MessageService.Success("已启用");
        RefreshStatus();
    }
    else
    {
        MessageService.Error($"启用失败: {result.Error?.UserMessage}");
    }
}

async Task DisableServer(string name)
{
    var result = await McpManager.DisableServerAsync(name);
    if (result.Success)
    {
        MessageService.Success("已禁用");
        RefreshStatus();
    }
    else
    {
        MessageService.Error($"禁用失败: {result.Error?.UserMessage}");
    }
}
```

---

### Task 6.5: 添加样式

**文件**: `samples/Seeing.Agent.WebUI/wwwroot/css/mcp-page.css` (新建)

```css
/* MCP 工具列表 */
.mcp-tool-list {
    margin-top: 12px;
    border-top: 1px solid var(--color-border);
    padding-top: 8px;
}

.mcp-tool-list .tool-list-header {
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
    font-size: 13px;
    color: var(--color-text-secondary);
}

.mcp-tool-list .tool-list-content {
    margin-top: 8px;
}

.mcp-tool-list .tool-item {
    padding: 8px 12px;
    background: var(--color-bg-subtle);
    border-radius: 4px;
    margin-bottom: 4px;
}

.mcp-tool-list .tool-name {
    display: flex;
    align-items: center;
    gap: 6px;
    font-weight: 500;
    font-size: 13px;
}

.mcp-tool-list .tool-desc {
    font-size: 12px;
    color: var(--color-text-secondary);
    margin-top: 4px;
}

/* JSON 编辑器 */
.mcp-json-editor {
    border: 1px solid var(--color-border);
    border-radius: 8px;
    overflow: hidden;
}

.mcp-json-editor .editor-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 8px 12px;
    background: var(--color-bg-subtle);
    border-bottom: 1px solid var(--color-border);
}

.mcp-json-editor .editor-body textarea {
    font-family: 'Consolas', 'Monaco', monospace;
    font-size: 13px;
    border: none;
}

.mcp-json-editor .editor-body textarea.json-error {
    border: 1px solid var(--color-error);
}

.mcp-json-editor .editor-error {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 12px;
    background: var(--color-error-bg);
    color: var(--color-error);
    font-size: 12px;
}

/* 配置路径信息 */
.config-path-info {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-top: 16px;
    padding: 12px;
    background: var(--color-bg-subtle);
    border-radius: 4px;
}

/* 禁用状态 */
.item-card.state-disabled {
    opacity: 0.6;
}
```

---

## Phase 7: 测试验证

### Task 7.1: 功能测试

```bash
# 构建
dotnet build src/Seeing.Agent --no-restore
dotnet build samples/Seeing.Agent.WebUI --no-restore

# 运行 WebUI
dotnet run --project samples/Seeing.Agent.WebUI
```

**测试清单**:

| 功能 | 操作 | 预期结果 |
|------|------|----------|
| JSON 编辑 | 点击"添加"，编辑 JSON，保存 | 配置保存并生效 |
| 编辑服务 | 点击"编辑"，修改 JSON，保存 | 配置更新并重连 |
| 删除服务 | 点击"删除"，确认 | 服务移除 |
| 启用服务 | 点击"启用" | 服务连接 |
| 禁用服务 | 点击"禁用" | 服务断开 |
| 工具展示 | 展开工具列表 | 显示工具名和描述 |
| 持久化 | 重启应用 | 配置保留 |

---

## 文件清单

| # | 操作 | 文件路径 | 说明 |
|---|------|----------|------|
| 1 | 新建 | `src/Seeing.Agent/MCP/Core/McpConfigLevel.cs` | 配置级别枚举 |
| 2 | 修改 | `src/Seeing.Agent/MCP/Core/McpErrorCode.cs` | 新增错误码 |
| 3 | 修改 | `src/Seeing.Agent/MCP/Core/McpOperationResult.cs` | Details 属性 |
| 4 | 修改 | `src/Seeing.Agent/MCP/Core/IMcpConfigManager.cs` | 完整接口 |
| 5 | 新建 | `src/Seeing.Agent/MCP/Configuration/IMcpConfigPersistence.cs` | 持久化接口 |
| 6 | 新建 | `src/Seeing.Agent/MCP/Configuration/McpConfigPersistence.cs` | 持久化实现 |
| 7 | 修改 | `src/Seeing.Agent/MCP/McpTool.cs` | ConfigLevel 字段 |
| 8 | 修改 | `src/Seeing.Agent/MCP/McpClientManager.cs` | 实现新方法 |
| 9 | 修改 | `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | DI 注册 |
| 10 | 新建 | `samples/Seeing.Agent.WebUI/Components/McpToolList.razor` | 工具列表组件 |
| 11 | 新建 | `samples/Seeing.Agent.WebUI/Components/McpJsonEditor.razor` | JSON 编辑器 |
| 12 | 修改 | `samples/Seeing.Agent.WebUI/Services/McpStateService.cs` | 扩展方法 |
| 13 | 修改 | `samples/Seeing.Agent.WebUI/Pages/McpPage.razor` | 页面重构 |
| 14 | 新建 | `samples/Seeing.Agent.WebUI/wwwroot/css/mcp-page.css` | 样式文件 |

---

## 验证命令

```bash
# 构建验证
dotnet build src/Seeing.Agent --no-restore

# 运行测试
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~Mcp"
```

---

## ✅ 审查问题修复确认

| 问题 | 状态 |
|------|------|
| P0-1: 兼容性构造函数未处理 `_configPersistence` | ✅ 已添加 `CreateDefaultConfigPersistence` |
| P0-2: `IMcpConfigManager` 接口缺少方法 | ✅ 完整定义所有方法 |
| P0-3: `ParseServerConfig` 未在接口定义 | ✅ 已添加到 `IMcpConfigPersistence` |
| P0-4: `McpStateService` 调用未定义方法 | ✅ 接口已定义 |
| P0-5: `McpPage.razor` 调用未定义方法 | ✅ 接口已定义 |
| P1-6: `EnableServerAsync` 线程安全 | ✅ 使用 `lock (_stateLock)` |
| P1-7: `ParseServerConfig` 实现未提供 | ✅ 完整实现 |
| P1-8: `SerializeServerConfig` 实现未提供 | ✅ 完整实现 |
| P1-9: `McpJsonEditor` 双向绑定 | ✅ 使用 `@bind-Value:event="oninput"` |
