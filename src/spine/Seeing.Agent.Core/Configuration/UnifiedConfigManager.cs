using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 统一配置管理器 - 处理所有配置的加载、合并、保存、变更通知
/// <para>
/// 配置层级：
/// - 用户级：~/.seeing/
/// - 项目级：{WorkspaceRoot}/.seeing/
/// </para>
/// </summary>
public sealed class UnifiedConfigManager : IConfigSectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(), new PluginSpecConverter() }
    };
    
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<UnifiedConfigManager> _logger;
    private readonly IConfigSectionRegistry _sectionRegistry;
    private readonly Dictionary<string, object> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly object _lock = new();
    
    // ===== 公开的配置属性 =====
    
    /// <summary>合并后的 SeeingAgent 配置</summary>
    public SeeingAgentOptions SeeingAgent { get; private set; } = new();

    /// <summary>用户级 SeeingAgent（未合并）。</summary>
    public SeeingAgentOptions? UserSeeingAgent { get; private set; }

    /// <summary>项目级 SeeingAgent（未合并）。</summary>
    public SeeingAgentOptions? ProjectSeeingAgent { get; private set; }
    
    /// <summary>配置变更事件（细粒度通知）</summary>
    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
    
    // ===== 构造函数 =====
    
    /// <summary>初始化统一配置管理器，注入工作区、日志器与配置节注册表。</summary>
    public UnifiedConfigManager(
        IWorkspaceProvider workspace,
        ILogger<UnifiedConfigManager> logger,
        IConfigSectionRegistry sectionRegistry)
    {
        _workspace = workspace;
        _logger = logger;
        _sectionRegistry = sectionRegistry ?? throw new ArgumentNullException(nameof(sectionRegistry));
    }
    
    // ===== 配置加载 =====
    
    /// <summary>加载所有配置</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        // 先构建完整新快照（局部变量），最后在锁内一次性替换，消除加载过程中的空缓存/半更新窗口。
        var userSeeing = await LoadFileAsync<SeeingAgentOptions>(ConfigLevel.User, "seeing.json", "SeeingAgent", ct);
        var projectSeeing = await LoadFileAsync<SeeingAgentOptions>(ConfigLevel.Project, "seeing.json", "SeeingAgent", ct);
        var mergedSeeing = MergeDeep.Merge(userSeeing ?? new(), projectSeeing ?? new());

        var newCache = new Dictionary<string, object>();

        // 能力包嵌套节 → newCache（不进入 SeeingAgentOptions）
        await CollectSeeingJsonSectionsToCacheAsync(newCache, ct);

        // 加载独立配置文件
        foreach (var meta in _sectionRegistry.Sections.Where(m => m.FileName != "seeing.json"))
        {
            await CollectSectionToCacheAsync(meta, newCache, ct);
        }

        lock (_lock)
        {
            UserSeeingAgent = userSeeing;
            ProjectSeeingAgent = projectSeeing;
            SeeingAgent = mergedSeeing;

            _cache.Clear();
            foreach (var (key, value) in newCache)
                _cache[key] = value;
        }

        _logger.LogInformation("配置已加载完成");
        OnConfigChanged(Array.Empty<string>());
    }

    private async Task CollectSeeingJsonSectionsToCacheAsync(
        IDictionary<string, object> target,
        CancellationToken ct)
    {
        foreach (var meta in _sectionRegistry.Sections.Where(m => m.FileName == "seeing.json"))
        {
            var userNode = await LoadSeeingNestedNodeAsync(ConfigLevel.User, meta.Key, ct);
            var projectNode = await LoadSeeingNestedNodeAsync(ConfigLevel.Project, meta.Key, ct);

            JsonNode? merged = meta.Scope switch
            {
                ConfigScope.UserOnly => userNode,
                ConfigScope.ProjectOnly => projectNode,
                _ => userNode is null && projectNode is null
                    ? null
                    : MergeJsonNodes(userNode ?? new JsonObject(), projectNode ?? new JsonObject())
            };

            if (merged is null)
                continue;

            target[meta.Key] = merged;
        }
    }

    private async Task<JsonNode?> LoadSeeingNestedNodeAsync(
        ConfigLevel level,
        string sectionName,
        CancellationToken ct)
    {
        var path = GetFilePath(level, "seeing.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var root = JsonNode.Parse(await ReadAllTextLockedAsync(path, ct)) as JsonObject;
            var seeing = root?["SeeingAgent"] as JsonObject;
            return seeing?[sectionName]?.DeepClone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载 seeing.json 嵌套节失败: {Section} @ {Level}", sectionName, level);
            return null;
        }
    }

    private async Task RemoveSeeingAgentKeysCoreAsync(
        ConfigLevel level,
        IEnumerable<string> keys,
        CancellationToken ct)
    {
        var path = GetFilePath(level, "seeing.json");
        if (!File.Exists(path))
            return;

        var fileLock = GetFileLock(path);
        await fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await LoadJsonRootAsync(path, ct).ConfigureAwait(false);
            if (root["SeeingAgent"] is not JsonObject seeingAgent)
                return;

            var removed = false;
            foreach (var key in keys)
            {
                if (seeingAgent.Remove(key))
                    removed = true;
            }

            if (removed)
                await WriteJsonAsync(path, root, ct).ConfigureAwait(false);
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 从 seeing.json 的 SeeingAgent 对象移除指定键并触发配置变更
    /// （用于清空 <c>Scenario</c> / <c>Boot</c> / <c>CapabilitySets</c> 等可选脊柱字段）。
    /// </summary>
    public async Task RemoveSeeingAgentKeysAsync(
        ConfigLevel level,
        IReadOnlyList<string> keys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await RemoveSeeingAgentKeysCoreAsync(level, keys, ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
        OnConfigChanged(keys.ToArray());
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
        if (!_sectionRegistry.TryGet(sectionName, out var meta))
            return null;
        
        ValidateScope(meta, level);
        
        if (meta.FileName == "seeing.json")
            return await LoadSeeingNestedAsync<T>(level, sectionName, ct);

        return await LoadFileAsync<T>(level, meta.FileName, null, ct);
    }

    private async Task<T?> LoadSeeingNestedAsync<T>(
        ConfigLevel level,
        string sectionName,
        CancellationToken ct) where T : class
    {
        var node = await LoadSeeingNestedNodeAsync(level, sectionName, ct);
        return node?.Deserialize<T>(JsonOptions);
    }
    
    /// <summary>检查指定级别的配置是否存在</summary>
    public bool HasSectionAtLevel(string sectionName, ConfigLevel level)
    {
        if (!_sectionRegistry.TryGet(sectionName, out var meta))
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
            HasUserLevel = (meta.Scope == ConfigScope.Both || meta.Scope == ConfigScope.UserOnly)
                && HasSectionAtLevel(sectionName, ConfigLevel.User),
            HasProjectLevel = meta.Scope != ConfigScope.UserOnly
                && HasSectionAtLevel(sectionName, ConfigLevel.Project),
            UserPath = (meta.Scope == ConfigScope.Both || meta.Scope == ConfigScope.UserOnly)
                ? GetFilePath(ConfigLevel.User, meta.FileName) : null,
            ProjectPath = meta.Scope != ConfigScope.UserOnly
                ? GetFilePath(ConfigLevel.Project, meta.FileName) ?? string.Empty : string.Empty,
            Scope = meta.Scope,
            ScopeReason = null
        };
    }
    
    // ===== 配置保存 =====
    
    /// <summary>保存配置节到指定级别</summary>
    public Task SaveSectionAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) where T : class
        => SaveSectionCoreAsync(sectionName, value, level, changedKeys: null, ct);

    /// <summary>
    /// 保存配置节并声明节内细粒度变更键（如 Providers 节下变更的 provider id），
    /// 供 ReloadHandler 做作用域刷新而非全量刷新。
    /// </summary>
    public Task SaveSectionAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level,
        IReadOnlyList<string>? changedKeys,
        CancellationToken ct = default) where T : class
        => SaveSectionCoreAsync(sectionName, value, level, changedKeys, ct);

    private async Task SaveSectionCoreAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level,
        IReadOnlyList<string>? changedKeys,
        CancellationToken ct) where T : class
    {
        if (!_sectionRegistry.TryGet(sectionName, out var meta))
            throw new ArgumentException($"未注册的配置节: {sectionName}");
        
        ValidateScope(meta, level);
        
        await SaveToFileAsync(level, meta.FileName, sectionName, value, ct);

        // Providers 为 UserOnly：任何用户级 Providers 写入（增/删/改模型、保存 Provider 连接）后，
        // 同步清理项目级残留的 Providers/ProviderModels 键，避免下次加载时被吸收合并导致"删除复活"。
        if (level == ConfigLevel.User && string.Equals(sectionName, "Providers", StringComparison.Ordinal))
        {
            await RemoveSeeingAgentKeysCoreAsync(ConfigLevel.Project, ["Providers", "ProviderModels"], ct)
                .ConfigureAwait(false);
        }

        // Acp 为 UserOnly：任何用户级 Acp 写入后，同步清理项目级残留的 Acp 键，
        // 避免下次加载时被吸收合并导致"删除复活"。
        if (level == ConfigLevel.User && string.Equals(sectionName, "Acp", StringComparison.Ordinal))
        {
            await RemoveSeeingAgentKeysCoreAsync(ConfigLevel.Project, ["Acp"], ct)
                .ConfigureAwait(false);
        }
        
        UpdateCache(sectionName, value, level);
        OnConfigChanged(new[] { sectionName }, changedKeys);
        
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
            if (_sectionRegistry.TryGet(name, out var meta))
                ValidateScope(meta, level);
        }
        
        // 按文件分组保存
        var byFile = sections.GroupBy(
            kv => _sectionRegistry.TryGet(kv.Key, out var m) ? m.FileName : "seeing.json");
        
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
        => _sectionRegistry.TryGet(sectionName, out var meta) ? meta : null;
    
    /// <summary>获取所有配置节注册信息</summary>
    public IReadOnlyDictionary<string, ConfigSectionMeta> GetAllSections()
    {
        var dict = new Dictionary<string, ConfigSectionMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in _sectionRegistry.Sections)
            dict[meta.Key] = meta;
        return dict;
    }
    
    // ===== IOptions 兼容 =====
    
    /// <summary>获取 SeeingAgentOptions（供 IOptions 使用）</summary>
    public SeeingAgentOptions GetSeeingAgentOptions() => SeeingAgent;

    /// <summary>
    /// 更新内存中的配置节（不写盘）。供测试与运行时热补丁使用。
    /// </summary>
    public void SetSectionInMemory<T>(string sectionName, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        UpdateCache(sectionName, value, ConfigLevel.User);
        OnConfigChanged(new[] { sectionName });
    }
    
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
        
        return await ReadAllTextLockedAsync(path, ct);
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

        // 复用文件锁：与 SaveToFileAsync/SaveMultipleToFileAsync 串行化同一文件的写入，避免相互覆盖
        var fileLock = GetFileLock(path);
        await fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteTextAsync(path, json, ct).ConfigureAwait(false);
        }
        finally
        {
            fileLock.Release();
        }
        
        // 重载配置
        await ReloadAsync(ct);
    }
    
    // ===== 私有方法 =====
    
    private void ValidateScope(ConfigSectionMeta meta, ConfigLevel level)
    {
        if (meta.Scope == ConfigScope.ProjectOnly && level == ConfigLevel.User)
        {
            throw new ConfigScopeException(meta.Key, level, meta.Scope);
        }

        if (meta.Scope == ConfigScope.UserOnly && level == ConfigLevel.Project)
        {
            throw new ConfigScopeException(meta.Key, level, meta.Scope);
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
            var json = await ReadAllTextLockedAsync(path, ct);
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
    
    private async Task CollectSectionToCacheAsync(
        ConfigSectionMeta meta,
        IDictionary<string, object> target,
        CancellationToken ct)
    {
        // 独立配置文件直接加载整个文件内容到目标快照
        if (meta.Scope == ConfigScope.ProjectOnly)
        {
            var path = GetFilePath(ConfigLevel.Project, meta.FileName);
            if (!File.Exists(path)) return;
            
            try
            {
                var json = await ReadAllTextLockedAsync(path, ct);
                var node = JsonNode.Parse(json);
                if (node != null)
                    target[meta.Key] = node;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载配置文件失败: {Path}", path);
            }
        }
        else if (meta.Scope == ConfigScope.UserOnly)
        {
            // 仅用户级：只加载用户级文件，不读取项目级
            var userPath = GetFilePath(ConfigLevel.User, meta.FileName);
            if (!File.Exists(userPath)) return;

            try
            {
                var node = JsonNode.Parse(await ReadAllTextLockedAsync(userPath, ct));
                if (node != null)
                    target[meta.Key] = node;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载用户级配置失败: {Path}", userPath);
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
                    userNode = JsonNode.Parse(await ReadAllTextLockedAsync(userPath, ct));
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
                    projectNode = JsonNode.Parse(await ReadAllTextLockedAsync(projectPath, ct));
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
                target[meta.Key] = merged;
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
        var fileLock = GetFileLock(path);
        await fileLock.WaitAsync(ct);
        try
        {
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
        finally
        {
            fileLock.Release();
        }
    }
    
    private async Task SaveMultipleToFileAsync(
        ConfigLevel level,
        string fileName,
        Dictionary<string, object> sections,
        CancellationToken ct)
    {
        var path = GetFilePath(level, fileName);
        var fileLock = GetFileLock(path);
        await fileLock.WaitAsync(ct);
        try
        {
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
                // 独立配置文件（providers.json、mcp.json 等）：每个文件仅承载一个节，
                // 与 SaveToFileAsync 语义一致，值直接替换根对象，避免多包一层 {SectionName}
                foreach (var (name, value) in sections)
                {
                    var serialized = JsonSerializer.SerializeToNode(value, JsonOptions);
                    if (serialized is JsonObject obj)
                        root = obj;
                    else
                        root[name] = serialized;
                    break;
                }
            }

            await WriteJsonAsync(path, root, ct);
        }
        finally
        {
            fileLock.Release();
        }
    }
    
    private SemaphoreSlim GetFileLock(string path)
        => _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// 在文件锁保护下读取文件文本。
    /// <para>
    /// 写路径使用 temp 文件 + <see cref="File.Move(string,string,bool)"/> 原子替换；Windows 上
    /// 目标文件若被并发读句柄（<see cref="FileShare.Read"/>，不含 Delete）打开，替换会失败并抛
    /// <see cref="UnauthorizedAccessException"/>。因此所有对可写配置文件的读取都必须与写路径共用同一
    /// <see cref="_fileLocks"/> 锁，保证读/写不重叠。注意：该方法不可在已持有同一文件锁的上下文中调用，
    /// 锁内读取请直接调用 <see cref="LoadJsonRootAsync"/>。
    /// </para>
    /// </summary>
    private async Task<string> ReadAllTextLockedAsync(string path, CancellationToken ct)
    {
        var fileLock = GetFileLock(path);
        await fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            fileLock.Release();
        }
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
        
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
    
    private string GetFilePath(ConfigLevel level, string fileName)
    {
        var dir = level == ConfigLevel.User 
            ? _workspace.UserSeeingDirectory 
            : _workspace.ProjectSeeingDirectory;
        return Path.Combine(dir, fileName);
    }
    
    private void UpdateCache(string sectionName, object value, ConfigLevel level)
    {
        _ = level;
        lock (_lock)
        {
            _cache[sectionName] = value;

            // 脊柱 Options：按属性名同步到 SeeingAgent（无 switch 到 god-object 字段列表）。
            // 以新实例替换而非原地反射修改，保证 SeeingAgent 快照对读者原子可见（不出现半更新对象）。
            var prop = typeof(SeeingAgentOptions).GetProperty(sectionName);
            var assignable = prop is { CanWrite: true } && value is not null &&
                (prop.PropertyType.IsInstanceOfType(value) ||
                 (value is string && prop.PropertyType == typeof(string)));

            if (assignable)
            {
                var snapshot = ShallowCloneOptions(SeeingAgent);
                prop!.SetValue(snapshot, value);
                SeeingAgent = snapshot;
            }
        }
    }

    /// <summary>
    /// 浅拷贝 SeeingAgentOptions：用于以新实例替换快照，避免原地修改被并发读者观察到中间态。
    /// </summary>
    private static SeeingAgentOptions ShallowCloneOptions(SeeingAgentOptions source)
    {
        var clone = new SeeingAgentOptions();
        foreach (var property in typeof(SeeingAgentOptions).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(clone, property.GetValue(source));
        }
        return clone;
    }
    
    private void OnConfigChanged(string[] changedSections, IReadOnlyList<string>? changedKeys = null)
    {
        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs
        {
            ChangedSections = changedSections,
            ChangedKeys = changedKeys ?? Array.Empty<string>()
        });
    }
}

/// <summary>配置来源信息</summary>
public sealed class ConfigSourceInfo
{
    /// <summary>配置节名称。</summary>
    public string SectionName { get; init; } = "";
    /// <summary>是否存在用户级配置。</summary>
    public bool HasUserLevel { get; init; }
    /// <summary>是否存在项目级配置。</summary>
    public bool HasProjectLevel { get; init; }
    /// <summary>用户级配置文件路径（若存在）。</summary>
    public string? UserPath { get; init; }
    /// <summary>项目级配置文件路径。</summary>
    public string ProjectPath { get; init; } = "";
    /// <summary>生效的配置作用域。</summary>
    public ConfigScope Scope { get; init; }
    /// <summary>作用域判定原因的补充说明（可选）。</summary>
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