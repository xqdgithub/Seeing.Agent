using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 用户级 / 项目级 .seeing/seeing.json 读写与配置重载。
/// </summary>
public sealed class SeeingConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SeeingAgentConfigurationProvider _configProvider;
    private readonly SchedulerOptionsProvider _schedulerOptionsProvider;
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<SeeingConfigService> _logger;

    public SeeingConfigService(
        SeeingAgentConfigurationProvider configProvider,
        SchedulerOptionsProvider schedulerOptionsProvider,
        IWorkspaceProvider workspace,
        ILogger<SeeingConfigService> logger)
    {
        _configProvider = configProvider;
        _schedulerOptionsProvider = schedulerOptionsProvider;
        _workspace = workspace;
        _logger = logger;
    }

    public string ProjectConfigPath => Path.Combine(_workspace.ProjectSeeingDirectory, "seeing.json");

    public string GetConfigPath(ConfigLevel level) =>
        Path.Combine(_workspace.GetSeeingDirectory(level), "seeing.json");

    /// <summary>加载合并后的生效配置（用户级 + 项目级，项目优先）。</summary>
    public Task<SeeingAgentOptions> LoadEffectiveOptionsAsync()
    {
        var clone = JsonSerializer.Deserialize<SeeingAgentOptions>(
            JsonSerializer.Serialize(_configProvider.Options, JsonOptions),
            JsonOptions) ?? new SeeingAgentOptions();
        return Task.FromResult(clone);
    }

    /// <summary>同 <see cref="LoadEffectiveOptionsAsync"/>（兼容旧调用）。</summary>
    public Task<SeeingAgentOptions> LoadProjectOptionsAsync() => LoadEffectiveOptionsAsync();

    /// <summary>读取指定级别配置文件中的 SeeingAgent 节。</summary>
    public async Task<T?> LoadLevelSectionAsync<T>(
        ConfigLevel level,
        string propertyName,
        CancellationToken ct = default)
    {
        var node = await LoadLevelPropertyNodeAsync(level, propertyName, ct);
        if (node is null)
            return default;

        return node.Deserialize<T>(JsonOptions);
    }

    public async Task<bool> HasLevelSectionAsync(
        ConfigLevel level,
        string propertyName,
        CancellationToken ct = default) =>
        await LoadLevelPropertyNodeAsync(level, propertyName, ct) != null;

    public AcpConfigSourceInfo GetAcpConfigSourceInfo()
    {
        var userPath = GetConfigPath(ConfigLevel.User);
        var projectPath = GetConfigPath(ConfigLevel.Project);
        return new AcpConfigSourceInfo
        {
            HasUserSection = HasPropertyInFile(userPath, "Acp"),
            HasProjectSection = HasPropertyInFile(projectPath, "Acp"),
            UserConfigPath = userPath,
            ProjectConfigPath = projectPath
        };
    }

    public SchedulerOptions GetSchedulerOptions() =>
        JsonSerializer.Deserialize<SchedulerOptions>(
            JsonSerializer.Serialize(_schedulerOptionsProvider.Current, JsonOptions),
            JsonOptions) ?? new SchedulerOptions();

    /// <summary>保存指定级别的单个 SeeingAgent 配置节。</summary>
    public async Task SaveLevelSectionAsync<T>(
        ConfigLevel level,
        string propertyName,
        T value,
        CancellationToken ct = default)
    {
        await PatchLevelSeeingAgentPropertyAsync(level, propertyName, value, ct);
        await ReloadConfigurationAsync(ct);
    }

    /// <summary>批量保存同一级别的多个 SeeingAgent 配置节（仅重载一次）。</summary>
    public async Task SaveLevelSectionsAsync(
        ConfigLevel level,
        IReadOnlyDictionary<string, object?> sections,
        CancellationToken ct = default)
    {
        foreach (var (propertyName, value) in sections)
        {
            if (value is null)
                continue;

            await PatchLevelPropertyAsync(level, propertyName, value, ct);
        }

        await ReloadConfigurationAsync(ct);
    }

    /// <summary>保存项目级配置节（默认 WebUI 行为）。</summary>
    public Task SaveProjectSectionAsync<T>(
        string propertyName,
        T value,
        CancellationToken ct = default) =>
        SaveLevelSectionAsync(ConfigLevel.Project, propertyName, value, ct);

    /// <summary>
    /// 兼容旧 API：按节 patch 到项目级。
    /// mutate 中应只修改需要持久化的顶层字段。
    /// </summary>
    public async Task SaveProjectOptionsAsync(Action<SeeingAgentOptions> mutate, CancellationToken ct = default)
    {
        var before = await LoadEffectiveOptionsAsync();
        var after = JsonSerializer.Deserialize<SeeingAgentOptions>(
            JsonSerializer.Serialize(before, JsonOptions),
            JsonOptions) ?? new SeeingAgentOptions();
        mutate(after);

        var sections = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!ReferenceEquals(before.DefaultProvider, after.DefaultProvider) && before.DefaultProvider != after.DefaultProvider)
            sections["DefaultProvider"] = after.DefaultProvider;
        if (!ReferenceEquals(before.DefaultModel, after.DefaultModel) && before.DefaultModel != after.DefaultModel)
            sections["DefaultModel"] = after.DefaultModel;
        if (!ReferenceEquals(before.DefaultAgent, after.DefaultAgent) && before.DefaultAgent != after.DefaultAgent)
            sections["DefaultAgent"] = after.DefaultAgent;
        if (before.Gateway != after.Gateway)
            sections["Gateway"] = after.Gateway;
        if (before.Acp != after.Acp)
            sections["Acp"] = after.Acp;
        if (before.Permission != after.Permission)
            sections["Permission"] = after.Permission;
        if (before.Providers != after.Providers)
            sections["Providers"] = after.Providers;
        if (before.Models != after.Models)
            sections["Models"] = after.Models;
        if (before.Agents != after.Agents)
            sections["Agents"] = after.Agents;
        if (before.PluginEnabled != after.PluginEnabled)
            sections["PluginEnabled"] = after.PluginEnabled;
        if (before.GatewayClients != after.GatewayClients)
            sections["GatewayClients"] = after.GatewayClients;

        if (sections.Count == 0)
            return;

        await SaveLevelSectionsAsync(ConfigLevel.Project, sections, ct);
    }

    public async Task SaveSchedulerOptionsAsync(Action<SchedulerOptions> mutate, CancellationToken ct = default)
    {
        var options = GetSchedulerOptions();
        mutate(options);
        await PatchLevelSeeingAgentPropertyAsync(ConfigLevel.Project, "Scheduler", options, ct);
        _schedulerOptionsProvider.Reload();
    }

    public async Task<string> GetRawProjectJsonAsync(CancellationToken ct = default)
    {
        var path = ProjectConfigPath;
        if (!File.Exists(path))
            return "{\n  \"SeeingAgent\": {}\n}";

        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task SaveRawProjectJsonAsync(string json, CancellationToken ct = default)
    {
        JsonDocument.Parse(json);
        await EnsureProjectDirectoryAsync();
        await File.WriteAllTextAsync(ProjectConfigPath, json, ct);
        await ReloadConfigurationAsync(ct);
    }

    /// <summary>保存项目级 GatewayClients 节（patch 单节，不重写整个 SeeingAgent）。</summary>
    public async Task SaveGatewayClientsSectionAsync(
        Action<SeeingAgentOptions> mutate,
        CancellationToken ct = default)
    {
        var gatewayClients = await LoadLevelSectionAsync<GatewayClientsOptions>(ConfigLevel.Project, "GatewayClients")
            ?? new GatewayClientsOptions();

        var wrapper = new SeeingAgentOptions { GatewayClients = gatewayClients };
        mutate(wrapper);
        await SaveLevelSectionAsync(ConfigLevel.Project, "GatewayClients", wrapper.GatewayClients, ct);
    }

    public Task ReloadConfigurationAsync(CancellationToken ct = default)
    {
        _configProvider.ReloadForWorkspace(_workspace.WorkspaceRoot, _logger);
        _schedulerOptionsProvider.Reload();
        return Task.CompletedTask;
    }

    private async Task<JsonNode?> LoadLevelPropertyNodeAsync(
        ConfigLevel level,
        string propertyName,
        CancellationToken ct)
    {
        var path = GetConfigPath(level);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        var root = JsonNode.Parse(json)?.AsObject();
        if (root == null)
            return null;

        var seeingAgent = root["SeeingAgent"] as JsonObject ?? root;
        return seeingAgent[propertyName];
    }

    private static bool HasPropertyInFile(string path, string propertyName)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (root == null)
                return false;

            var seeingAgent = root["SeeingAgent"] as JsonObject ?? root;
            return seeingAgent[propertyName] != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task PatchLevelSeeingAgentPropertyAsync<T>(
        ConfigLevel level,
        string propertyName,
        T value,
        CancellationToken ct)
    {
        await PatchLevelPropertyAsync(level, propertyName, value!, ct);
    }

    private async Task PatchLevelPropertyAsync(
        ConfigLevel level,
        string propertyName,
        object value,
        CancellationToken ct)
    {
        var path = GetConfigPath(level);
        var root = await LoadRootNodeAsync(path, ct);
        var seeingAgent = root["SeeingAgent"] as JsonObject ?? new JsonObject();
        seeingAgent[propertyName] = JsonSerializer.SerializeToNode(value, JsonOptions);
        root["SeeingAgent"] = seeingAgent;
        await WriteRootNodeAsync(path, root, level, propertyName, ct);
    }

    private async Task<JsonObject> LoadRootNodeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new JsonObject();

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    private async Task WriteRootNodeAsync(
        string path,
        JsonObject root,
        ConfigLevel level,
        string propertyName,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
        _logger.LogInformation(
            "已保存{Level}配置节 {Property}: {Path}",
            level == ConfigLevel.User ? "用户级" : "项目级",
            propertyName,
            path);
    }

    /// <summary>读取合并后的 ACP 配置（避免整个 SeeingAgentOptions 序列化往返）。</summary>
    public Task<AcpOptions> LoadEffectiveAcpAsync()
    {
        var acp = _configProvider.Options.Acp;
        var clone = JsonSerializer.Deserialize<AcpOptions>(
            JsonSerializer.Serialize(acp, JsonOptions),
            JsonOptions) ?? new AcpOptions();
        return Task.FromResult(clone);
    }

    /// <summary>保存 ACP 配置并在写入后校验磁盘内容。</summary>
    public async Task SaveAcpSectionAsync(
        ConfigLevel level,
        AcpOptions value,
        CancellationToken ct = default)
    {
        await SaveLevelSectionAsync(level, "Acp", value, ct);

        var saved = await LoadLevelSectionAsync<AcpOptions>(level, "Acp", ct)
            ?? throw new InvalidOperationException($"写入后无法读取 ACP 配置: {GetConfigPath(level)}");

        if (saved.Enabled != value.Enabled ||
            !string.Equals(saved.DefaultBackend, value.DefaultBackend, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ACP 配置写入校验失败: {GetConfigPath(level)}");
        }

        foreach (var (backendId, expectedBackend) in value.Backends)
        {
            var actualBackend = saved.Backends
                .FirstOrDefault(kv => string.Equals(kv.Key, backendId, StringComparison.OrdinalIgnoreCase))
                .Value;
            if (actualBackend is null)
                throw new InvalidOperationException($"ACP 后端 '{backendId}' 未写入配置文件");

            if (!string.Equals(actualBackend.Command, expectedBackend.Command, StringComparison.Ordinal))
                throw new InvalidOperationException($"ACP 后端 '{backendId}' 命令写入不一致");
        }
    }

    private async Task EnsureProjectDirectoryAsync()
    {
        var dir = Path.GetDirectoryName(ProjectConfigPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await Task.CompletedTask;
    }
}

public sealed class AcpConfigSourceInfo
{
    public bool HasUserSection { get; init; }

    public bool HasProjectSection { get; init; }

    public string UserConfigPath { get; init; } = "";

    public string ProjectConfigPath { get; init; } = "";
}
