using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.MCP;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 统一配置管理器 - 处理所有配置的加载、合并、保存、变更通知
/// <para>
/// 配置层级：
/// - 用户级：~/.seeing/
/// - 项目级：{WorkspaceRoot}/.seeing/
/// </para>
/// </summary>
public sealed class UnifiedConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };
    
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<UnifiedConfigManager> _logger;
    private readonly Dictionary<string, ConfigSectionMeta> _sectionRegistry;
    private readonly Dictionary<string, object> _cache = new();
    private readonly object _lock = new();
    
    // ===== 公开的配置属性 =====
    
    /// <summary>合并后的 SeeingAgent 配置</summary>
    public SeeingAgentOptions SeeingAgent { get; private set; } = new();
    
    /// <summary>配置变更事件（细粒度通知）</summary>
    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
    
    // ===== 构造函数 =====
    
    public UnifiedConfigManager(
        IWorkspaceProvider workspace,
        ILogger<UnifiedConfigManager> logger)
    {
        _workspace = workspace;
        _logger = logger;
        _sectionRegistry = BuildSectionRegistry();
    }
    
    // ===== 配置节注册 =====
    
    private Dictionary<string, ConfigSectionMeta> BuildSectionRegistry()
    {
        return new Dictionary<string, ConfigSectionMeta>
        {
            // seeing.json 内的配置（双层级）
            ["DefaultProvider"] = new("DefaultProvider", "seeing.json", ConfigScope.Both, 
                typeof(string), displayName: "默认 Provider", displayOrder: 1),
            ["DefaultModel"] = new("DefaultModel", "seeing.json", ConfigScope.Both, 
                typeof(string), displayName: "默认模型", displayOrder: 2),
            ["DefaultAgent"] = new("DefaultAgent", "seeing.json", ConfigScope.Both, 
                typeof(string), displayName: "默认智能体", displayOrder: 3),
            ["Providers"] = new("Providers", "seeing.json", ConfigScope.Both, 
                typeof(Dictionary<string, ProviderConfig>), displayName: "Provider 配置", displayOrder: 4),
            ["Models"] = new("Models", "seeing.json", ConfigScope.Both, 
                typeof(Dictionary<string, ModelConfig>), displayName: "模型配置", displayOrder: 5),
            ["ModelScope"] = new("ModelScope", "seeing.json", ConfigScope.Both, 
                typeof(ModelScopeSection), displayName: "ModelScope 配置", displayOrder: 6),
            ["Agents"] = new("Agents", "seeing.json", ConfigScope.Both, 
                typeof(Dictionary<string, AgentConfig>), displayName: "智能体配置", displayOrder: 7),
            ["Acp"] = new("Acp", "seeing.json", ConfigScope.Both, 
                typeof(AcpOptions), displayName: "ACP 配置", displayOrder: 7),
            ["Plugins"] = new("Plugins", "seeing.json", ConfigScope.Both, 
                typeof(List<PluginSpec>), displayName: "插件列表", displayOrder: 8),
            ["PluginEnabled"] = new("PluginEnabled", "seeing.json", ConfigScope.Both, 
                typeof(Dictionary<string, bool>), displayName: "插件启用状态", displayOrder: 9),
            ["Skills"] = new("Skills", "seeing.json", ConfigScope.Both, 
                typeof(SkillsConfig), displayName: "技能配置", displayOrder: 10),
            
            // seeing.json 内的配置（仅项目级）
            ["Gateway"] = new("Gateway", "seeing.json", ConfigScope.ProjectOnly, 
                typeof(GatewayOptions), 
                scopeReason: "Gateway 服务端口绑定与项目运行环境相关", 
                displayName: "Gateway 配置", displayOrder: 11),
            ["GatewayClients"] = new("GatewayClients", "seeing.json", ConfigScope.ProjectOnly, 
                typeof(GatewayClientsOptions), 
                scopeReason: "Gateway Client 配置与项目运行环境相关", 
                displayName: "Gateway Clients 配置", displayOrder: 12),
            ["Permission"] = new("Permission", "seeing.json", ConfigScope.ProjectOnly, 
                typeof(PermissionOptions), 
                scopeReason: "权限策略与项目安全上下文绑定", 
                displayName: "权限配置", displayOrder: 13),
            ["Workspace"] = new("Workspace", "seeing.json", ConfigScope.ProjectOnly, 
                typeof(WorkspaceOptions), 
                scopeReason: "工作区配置与项目运行环境绑定", 
                displayName: "工作区配置", displayOrder: 14),
            
            // 独立配置文件（仅项目级）
            ["Scheduler"] = new("Scheduler", "scheduler.json", ConfigScope.ProjectOnly, 
                typeof(object),
                scopeReason: "任务调度与项目工作流绑定，避免循环依赖", 
                displayName: "调度器配置", displayOrder: 13),
            
            // 独立配置文件（双层级）
            ["Mcp"] = new("Mcp", "mcp.json", ConfigScope.Both, 
                typeof(Dictionary<string, McpServerConfig>), displayName: "MCP 服务器", displayOrder: 14),
            ["Memory"] = new("Memory", "memory.json", ConfigScope.Both, 
                typeof(object),  // Memory 在独立模块中定义
                displayName: "Memory 配置", displayOrder: 15),
        };
    }
    
    // ===== 配置加载 =====
    
    /// <summary>加载所有配置</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _cache.Clear();
        }
        
        // 加载 seeing.json
        var userSeeing = await LoadFileAsync<SeeingAgentOptions>(ConfigLevel.User, "seeing.json", "SeeingAgent", ct);
        var projectSeeing = await LoadFileAsync<SeeingAgentOptions>(ConfigLevel.Project, "seeing.json", "SeeingAgent", ct);
        SeeingAgent = MergeDeep.Merge(userSeeing ?? new(), projectSeeing ?? new());
        
        // 加载独立配置文件
        foreach (var meta in _sectionRegistry.Values.Where(m => m.FileName != "seeing.json"))
        {
            await LoadSectionToCacheAsync(meta, ct);
        }
        
        _logger.LogInformation("配置已加载完成");
        OnConfigChanged(Array.Empty<string>());
    }
    
    /// <summary>重载配置（外部文件变更时调用）</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("开始重载配置...");
        await LoadAsync(ct);
    }
    
    // ===== 配置读取 =====
    
    /// <summary>获取合并后的配置节</summary>
    public T GetSection<T>(string sectionName) where T : class, new()
    {
        lock (_lock)
        {
            // seeing.json 内的配置从 SeeingAgent 属性获取
            if (_sectionRegistry.TryGetValue(sectionName, out var meta) && meta.FileName == "seeing.json")
            {
                return GetFromSeeingAgent<T>(sectionName) ?? new T();
            }
            
            // 独立配置从缓存获取（JsonNode 需要反序列化）
            if (_cache.TryGetValue(sectionName, out var value))
            {
                if (value is T typed)
                    return typed;
                
                if (value is JsonNode node)
                {
                    var deserialized = node.Deserialize<T>(JsonOptions);
                    if (deserialized != null)
                        return deserialized;
                }
            }
            
            return new T();
        }
    }
    
    /// <summary>获取指定级别的配置节（不合并，用于查看来源）</summary>
    public async Task<T?> GetSectionAtLevelAsync<T>(
        string sectionName,
        ConfigLevel level,
        CancellationToken ct = default) where T : class
    {
        if (!_sectionRegistry.TryGetValue(sectionName, out var meta))
            return null;
        
        ValidateScope(meta, level);
        
        return await LoadFileAsync<T>(level, meta.FileName, 
            meta.FileName == "seeing.json" ? "SeeingAgent" : null, ct);
    }
    
    /// <summary>检查指定级别的配置是否存在</summary>
    public bool HasSectionAtLevel(string sectionName, ConfigLevel level)
    {
        if (!_sectionRegistry.TryGetValue(sectionName, out var meta))
            return false;
        
        var path = GetFilePath(level, meta.FileName);
        return File.Exists(path);
    }
    
    /// <summary>获取配置来源信息</summary>
    public ConfigSourceInfo GetSourceInfo(string sectionName)
    {
        var meta = GetSectionMeta(sectionName);
        if (meta == null)
            return new ConfigSourceInfo { SectionName = sectionName };
        
        return new ConfigSourceInfo
        {
            SectionName = sectionName,
            HasUserLevel = meta.Scope == ConfigScope.Both && HasSectionAtLevel(sectionName, ConfigLevel.User),
            HasProjectLevel = HasSectionAtLevel(sectionName, ConfigLevel.Project),
            UserPath = meta.Scope == ConfigScope.Both ? GetFilePath(ConfigLevel.User, meta.FileName) : null,
            ProjectPath = GetFilePath(ConfigLevel.Project, meta.FileName),
            Scope = meta.Scope,
            ScopeReason = meta.ScopeReason
        };
    }
    
    // ===== 配置保存 =====
    
    /// <summary>保存配置节到指定级别</summary>
    public async Task SaveSectionAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) where T : class
    {
        if (!_sectionRegistry.TryGetValue(sectionName, out var meta))
            throw new ArgumentException($"未注册的配置节: {sectionName}");
        
        ValidateScope(meta, level);
        
        await SaveToFileAsync(level, meta.FileName, sectionName, value, ct);
        
        UpdateCache(sectionName, value, level);
        OnConfigChanged(new[] { sectionName });
        
        _logger.LogInformation("配置节 {Section} 已保存到 {Level}级", sectionName, level == ConfigLevel.User ? "用户" : "项目");
    }
    
    /// <summary>批量保存配置节到同一级别</summary>
    public async Task SaveSectionsAsync(
        ConfigLevel level,
        IReadOnlyDictionary<string, object> sections,
        CancellationToken ct = default)
    {
        // 验证所有配置节
        foreach (var name in sections.Keys)
        {
            if (_sectionRegistry.TryGetValue(name, out var meta))
                ValidateScope(meta, level);
        }
        
        // 按文件分组保存
        var byFile = sections.GroupBy(
            kv => _sectionRegistry.TryGetValue(kv.Key, out var m) ? m.FileName : "seeing.json");
        
        foreach (var group in byFile)
        {
            await SaveMultipleToFileAsync(level, group.Key, group.ToDictionary(), ct);
        }
        
        // 更新缓存
        foreach (var (name, value) in sections)
        {
            UpdateCache(name, value, level);
        }
        
        OnConfigChanged(sections.Keys.ToArray());
        _logger.LogInformation("已保存 {Count} 个配置节到 {Level}级", sections.Count, level == ConfigLevel.User ? "用户" : "项目");
    }
    
    // ===== 配置节元信息 =====
    
    /// <summary>获取配置节元信息</summary>
    public ConfigSectionMeta? GetSectionMeta(string sectionName)
        => _sectionRegistry.TryGetValue(sectionName, out var meta) ? meta : null;
    
    /// <summary>获取所有配置节注册信息</summary>
    public IReadOnlyDictionary<string, ConfigSectionMeta> GetAllSections() => _sectionRegistry;
    
    // ===== IOptions 兼容 =====
    
    /// <summary>获取 SeeingAgentOptions（供 IOptions 使用）</summary>
    public SeeingAgentOptions GetSeeingAgentOptions() => SeeingAgent;
    
    /// <summary>获取 GatewayOptions（供 IOptions 使用）</summary>
    public GatewayOptions GetGatewayOptions() => SeeingAgent.Gateway;
    
    /// <summary>获取指定级别的 SeeingAgentOptions（不合并）</summary>
    public async Task<SeeingAgentOptions?> GetSeeingAgentOptionsAtLevelAsync(
        ConfigLevel level,
        CancellationToken ct = default)
    {
        return await LoadFileAsync<SeeingAgentOptions>(level, "seeing.json", "SeeingAgent", ct);
    }
    
    // ===== 原始 JSON 访问 =====
    
    /// <summary>获取指定文件的原始 JSON</summary>
    public async Task<string> GetRawJsonAsync(
        ConfigLevel level,
        string fileName = "seeing.json",
        CancellationToken ct = default)
    {
        var path = GetFilePath(level, fileName);
        if (!File.Exists(path))
        {
            if (fileName == "seeing.json")
                return "{\n  \"SeeingAgent\": {}\n}";
            return "{}";
        }
        
        return await File.ReadAllTextAsync(path, ct);
    }
    
    /// <summary>保存原始 JSON</summary>
    public async Task SaveRawJsonAsync(
        ConfigLevel level,
        string fileName,
        string json,
        CancellationToken ct = default)
    {
        // 验证 JSON 格式
        try
        {
            JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"JSON 格式错误: {ex.Message}");
        }
        
        var path = GetFilePath(level, fileName);
        await WriteTextAsync(path, json, ct);
        
        // 重载配置
        await ReloadAsync(ct);
    }
    
    // ===== 私有方法 =====
    
    private void ValidateScope(ConfigSectionMeta meta, ConfigLevel level)
    {
        if (meta.Scope == ConfigScope.ProjectOnly && level == ConfigLevel.User)
        {
            throw new ConfigScopeException(meta.SectionName, level, meta.Scope, meta.ScopeReason);
        }
    }
    
    private async Task<T?> LoadFileAsync<T>(
        ConfigLevel level,
        string fileName,
        string? rootSection,
        CancellationToken ct) where T : class
    {
        var path = GetFilePath(level, fileName);
        if (!File.Exists(path)) return null;
        
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            
            var element = doc.RootElement;
            if (rootSection != null && element.TryGetProperty(rootSection, out var section))
                element = section;
            
            return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载配置文件失败: {Path}", path);
            return null;
        }
    }
    
    private async Task LoadSectionToCacheAsync(ConfigSectionMeta meta, CancellationToken ct)
    {
        // 独立配置文件直接加载整个文件内容到缓存
        // GetSection<T> 会在调用时进行类型转换
        if (meta.Scope == ConfigScope.ProjectOnly)
        {
            var path = GetFilePath(ConfigLevel.Project, meta.FileName);
            if (!File.Exists(path)) return;
            
            try
            {
                var json = await File.ReadAllTextAsync(path, ct);
                var node = JsonNode.Parse(json);
                if (node != null)
                    _cache[meta.SectionName] = node;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载配置文件失败: {Path}", path);
            }
        }
        else
        {
            var userPath = GetFilePath(ConfigLevel.User, meta.FileName);
            var projectPath = GetFilePath(ConfigLevel.Project, meta.FileName);
            
            JsonNode? userNode = null;
            JsonNode? projectNode = null;
            
            if (File.Exists(userPath))
            {
                try
                {
                    userNode = JsonNode.Parse(await File.ReadAllTextAsync(userPath, ct));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "加载用户级配置失败: {Path}", userPath);
                }
            }
            
            if (File.Exists(projectPath))
            {
                try
                {
                    projectNode = JsonNode.Parse(await File.ReadAllTextAsync(projectPath, ct));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "加载项目级配置失败: {Path}", projectPath);
                }
            }
            
            if (userNode != null || projectNode != null)
            {
                // 合并两个 JsonNode
                var merged = MergeJsonNodes(userNode ?? new JsonObject(), projectNode ?? new JsonObject());
                _cache[meta.SectionName] = merged;
            }
        }
    }
    
    private static JsonNode MergeJsonNodes(JsonNode? baseNode, JsonNode? overrideNode)
    {
        if (baseNode is null) return overrideNode?.DeepClone() ?? new JsonObject();
        if (overrideNode is null) return baseNode.DeepClone();
        
        if (baseNode is not JsonObject baseObj || overrideNode is not JsonObject overrideObj)
            return overrideNode.DeepClone();
        
        var result = (JsonObject)baseObj.DeepClone();
        
        foreach (var kvp in overrideObj)
        {
            if (result.ContainsKey(kvp.Key))
            {
                if (result[kvp.Key] is JsonObject baseChild && kvp.Value is JsonObject overrideChild)
                {
                    result[kvp.Key] = MergeJsonNodes(baseChild, overrideChild);
                }
                else
                {
                    result[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
            else
            {
                result[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        
        return result;
    }
    
    private async Task SaveToFileAsync<T>(
        ConfigLevel level,
        string fileName,
        string sectionName,
        T value,
        CancellationToken ct) where T : class
    {
        var path = GetFilePath(level, fileName);
        var root = await LoadJsonRootAsync(path, ct);
        
        if (fileName == "seeing.json")
        {
            var seeingAgent = root["SeeingAgent"] as JsonObject ?? new JsonObject();
            seeingAgent[sectionName] = JsonSerializer.SerializeToNode(value, JsonOptions);
            root["SeeingAgent"] = seeingAgent;
        }
        else
        {
            // 独立配置文件，直接替换根
            var serialized = JsonSerializer.SerializeToNode(value, JsonOptions);
            if (serialized is JsonObject obj)
                root = obj;
            else
                root[sectionName] = serialized;
        }
        
        await WriteJsonAsync(path, root, ct);
    }
    
    private async Task SaveMultipleToFileAsync(
        ConfigLevel level,
        string fileName,
        Dictionary<string, object> sections,
        CancellationToken ct)
    {
        var path = GetFilePath(level, fileName);
        var root = await LoadJsonRootAsync(path, ct);
        
        if (fileName == "seeing.json")
        {
            var seeingAgent = root["SeeingAgent"] as JsonObject ?? new JsonObject();
            foreach (var (name, value) in sections)
            {
                seeingAgent[name] = JsonSerializer.SerializeToNode(value, JsonOptions);
            }
            root["SeeingAgent"] = seeingAgent;
        }
        else
        {
            // 独立配置文件：mcp.json、memory.json、scheduler.json 等
            foreach (var (name, value) in sections)
            {
                root[name] = JsonSerializer.SerializeToNode(value, JsonOptions);
            }
        }
        
        await WriteJsonAsync(path, root, ct);
    }
    
    private async Task<JsonObject> LoadJsonRootAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new JsonObject();
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }
    
    private async Task WriteJsonAsync(string path, JsonObject root, CancellationToken ct)
    {
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await WriteTextAsync(path, json, ct);
    }
    
    private async Task WriteTextAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, ct);
        File.Move(tempPath, path, overwrite: true);
    }
    
    private string GetFilePath(ConfigLevel level, string fileName)
    {
        var dir = level == ConfigLevel.User 
            ? _workspace.UserSeeingDirectory 
            : _workspace.ProjectSeeingDirectory;
        return Path.Combine(dir, fileName);
    }
    
    private T? GetFromSeeingAgent<T>(string sectionName) where T : class
    {
        return sectionName switch
        {
            "DefaultProvider" => SeeingAgent.DefaultProvider as T,
            "DefaultModel" => SeeingAgent.DefaultModel as T,
            "DefaultAgent" => SeeingAgent.DefaultAgent as T,
            "Providers" => SeeingAgent.Providers as T,
            "Models" => SeeingAgent.Models as T,
            "ModelScope" => SeeingAgent.ModelScope as T,
            "Agents" => SeeingAgent.Agents as T,
            "Gateway" => SeeingAgent.Gateway as T,
            "GatewayClients" => SeeingAgent.GatewayClients as T,
            "Acp" => SeeingAgent.Acp as T,
            "Plugins" => SeeingAgent.Plugins as T,
            "PluginEnabled" => SeeingAgent.PluginEnabled as T,
            "Skills" => SeeingAgent.Skills as T,
            "Permission" => SeeingAgent.Permission as T,
            "Workspace" => SeeingAgent.Workspace as T,
            _ => null
        };
    }
    
    private void UpdateCache(string sectionName, object value, ConfigLevel level)
    {
        lock (_lock)
        {
            // seeing.json 内的配置需要更新 SeeingAgent 属性
            if (_sectionRegistry.TryGetValue(sectionName, out var meta) && meta.FileName == "seeing.json")
            {
                UpdateSeeingAgentProperty(sectionName, value);
            }
            
            _cache[sectionName] = value;
        }
    }
    
    private void UpdateSeeingAgentProperty(string sectionName, object value)
    {
        switch (sectionName)
        {
            case "DefaultProvider":
                SeeingAgent.DefaultProvider = value as string;
                break;
            case "DefaultModel":
                SeeingAgent.DefaultModel = value as string;
                break;
            case "DefaultAgent":
                SeeingAgent.DefaultAgent = value as string;
                break;
            case "Providers":
                if (value is Dictionary<string, ProviderConfig> providers)
                    SeeingAgent.Providers = providers;
                break;
            case "Models":
                if (value is Dictionary<string, ModelConfig> models)
                    SeeingAgent.Models = models;
                break;
            case "ModelScope":
                if (value is ModelScopeSection modelScope)
                    SeeingAgent.ModelScope = modelScope;
                break;
            case "Agents":
                if (value is Dictionary<string, AgentConfig> agents)
                    SeeingAgent.Agents = agents;
                break;
            case "Gateway":
                if (value is GatewayOptions gateway)
                    SeeingAgent.Gateway = gateway;
                break;
            case "GatewayClients":
                if (value is GatewayClientsOptions gatewayClients)
                    SeeingAgent.GatewayClients = gatewayClients;
                break;
            case "Acp":
                if (value is AcpOptions acp)
                    SeeingAgent.Acp = acp;
                break;
            case "Plugins":
                if (value is List<PluginSpec> plugins)
                    SeeingAgent.Plugins = plugins;
                break;
            case "PluginEnabled":
                if (value is Dictionary<string, bool> enabled)
                    SeeingAgent.PluginEnabled = enabled;
                break;
            case "Skills":
                if (value is SkillsConfig skills)
                    SeeingAgent.Skills = skills;
                break;
            case "Permission":
                if (value is PermissionOptions permission)
                    SeeingAgent.Permission = permission;
                break;
            case "Workspace":
                if (value is WorkspaceOptions workspace)
                    SeeingAgent.Workspace = workspace;
                break;
        }
    }
    
    private void OnConfigChanged(string[] changedSections)
    {
        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs
        {
            ChangedSections = changedSections
        });
    }
}

/// <summary>配置来源信息</summary>
public sealed class ConfigSourceInfo
{
    public string SectionName { get; init; } = "";
    public bool HasUserLevel { get; init; }
    public bool HasProjectLevel { get; init; }
    public string? UserPath { get; init; }
    public string ProjectPath { get; init; } = "";
    public ConfigScope Scope { get; init; }
    public string? ScopeReason { get; init; }
    
    /// <summary>有效配置来源描述</summary>
    public string SourceDescription
    {
        get
        {
            if (HasUserLevel && HasProjectLevel)
                return "项目级（覆盖用户级）";
            if (HasProjectLevel)
                return "项目级";
            if (HasUserLevel)
                return "用户级";
            return "默认值";
        }
    }
}