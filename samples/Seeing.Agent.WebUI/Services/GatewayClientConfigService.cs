using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;
using Seeing.Gateway.Client;
using Seeing.Gateway.Plugins;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Gateway Client 配置读写与 Channel 运行时 JSON 生成。
/// Channel 参数写入 <c>.seeing/gateway-clients/{channelId}.json</c>；
/// <c>seeing.json</c> 仅保留启用状态与插件引用。
/// </summary>
public sealed class GatewayClientConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly UnifiedConfigManager _configManager;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly GatewayChannelRegistry _registry;
    private readonly ILogger<GatewayClientConfigService> _logger;
    private readonly string _clientsDirectory;

    public GatewayClientConfigService(
        UnifiedConfigManager configManager,
        IWorkspaceProvider workspaceProvider,
        GatewayChannelRegistry registry,
        ILogger<GatewayClientConfigService> logger)
    {
        _configManager = configManager;
        _workspaceProvider = workspaceProvider;
        _registry = registry;
        _logger = logger;
        _clientsDirectory = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "gateway-clients");
    }

    public string ClientsDirectory => _clientsDirectory;

    public async Task<IReadOnlyList<GatewayClientViewModel>> GetClientsAsync(CancellationToken ct = default)
    {
        var gatewayClients = _configManager.GetSection<GatewayClientsOptions>("GatewayClients");
        var serverGateway = _configManager.GetGatewayOptions();
        var result = new List<GatewayClientViewModel>();

        foreach (var typeInfo in _registry.Types)
        {
            gatewayClients.Channels.TryGetValue(typeInfo.ChannelId, out var entry);
            entry ??= new GatewayChannelEntry
            {
                PluginSpec = typeInfo.PluginSpec
            };

            var snapshot = await LoadChannelConfigAsync(
                typeInfo,
                entry,
                gatewayClients.Defaults,
                serverGateway,
                ct);

            if (entry.Options is { Count: > 0 })
                await MigrateLegacyConfigAsync(typeInfo, entry, snapshot, ct);

            result.Add(new GatewayClientViewModel
            {
                ChannelId = typeInfo.ChannelId,
                DisplayName = typeInfo.DisplayName,
                Description = typeInfo.Description,
                IsBuiltin = typeInfo.IsBuiltin,
                PluginSpec = entry.PluginSpec ?? typeInfo.PluginSpec,
                Enabled = snapshot.Enabled,
                Gateway = snapshot.Gateway,
                Agent = snapshot.Agent,
                Model = snapshot.Model,
                Mode = snapshot.Mode,
                Options = snapshot.Options,
                Fields = typeInfo.Fields,
                ConfigFormComponentType = typeInfo.ConfigFormComponentType,
                AssemblyPath = typeInfo.AssemblyPath,
                ConfigFilePath = GetRuntimeConfigPath(typeInfo.ChannelId)
            });
        }

        return result;
    }

    public async Task SaveClientAsync(GatewayClientViewModel model, CancellationToken ct = default)
    {
        var typeInfo = _registry.GetTypeInfo(model.ChannelId)
            ?? throw new InvalidOperationException($"未知 Channel: {model.ChannelId}");

        ValidateOptions(typeInfo, model.Options);

        await WriteChannelConfigAsync(typeInfo, model, ct);

        var gatewayClients = _configManager.GetSection<GatewayClientsOptions>("GatewayClients");
        gatewayClients.Channels[model.ChannelId] = CreateRegistryEntry(model, typeInfo);
        await _configManager.SaveSectionAsync("GatewayClients", gatewayClients, ConfigLevel.Project, ct);

        _logger.LogInformation(
            "已保存 Gateway Client 配置: {ChannelId} -> {ConfigPath}",
            model.ChannelId,
            GetRuntimeConfigPath(model.ChannelId));
    }

    public async Task SetClientEnabledAsync(string channelId, bool enabled, CancellationToken ct = default)
    {
        var clients = await GetClientsAsync(ct);
        var client = clients.FirstOrDefault(c => c.ChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未知 Channel: {channelId}");

        if (client.Enabled == enabled)
            return;

        client.Enabled = enabled;
        await SaveClientAsync(client, ct);
    }

    public string GetRuntimeConfigPath(string channelId) =>
        Path.Combine(_clientsDirectory, $"{channelId}.json");

    public string GetRuntimeStatePath(string channelId) =>
        Path.Combine(_clientsDirectory, $"{channelId}.state.json");

    public async Task<GatewayClientRuntimeState> LoadRuntimeStateAsync(string channelId, CancellationToken ct = default)
    {
        var path = GetRuntimeStatePath(channelId);
        if (!File.Exists(path))
            return new GatewayClientRuntimeState();

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<GatewayClientRuntimeState>(json, JsonOptions)
               ?? new GatewayClientRuntimeState();
    }

    public async Task SaveRuntimeStateAsync(string channelId, GatewayClientRuntimeState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_clientsDirectory);
        var path = GetRuntimeStatePath(channelId);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    public void ReloadRegistry() =>
        _registry.Reload(_workspaceProvider.WorkspaceRoot);

    public async Task InstallPluginAsync(string sourceDllPath, CancellationToken ct = default)
    {
        var targetDir = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "gateway-channels");
        Directory.CreateDirectory(targetDir);
        var fileName = Path.GetFileName(sourceDllPath);
        var targetPath = Path.Combine(targetDir, fileName);
        File.Copy(sourceDllPath, targetPath, overwrite: true);
        ReloadRegistry();

        var gatewayClients = _configManager.GetSection<GatewayClientsOptions>("GatewayClients");
        var spec = $"file://{targetPath.Replace('\\', '/')}";
        if (!gatewayClients.Plugins.Any(p => p.Spec.Equals(spec, StringComparison.OrdinalIgnoreCase)))
        {
            gatewayClients.Plugins.Add(new PluginSpec { Spec = spec });
            await _configManager.SaveSectionAsync("GatewayClients", gatewayClients, ConfigLevel.Project, ct);
        }
    }

    private async Task<ChannelConfigSnapshot> LoadChannelConfigAsync(
        GatewayChannelTypeInfo typeInfo,
        GatewayChannelEntry entry,
        GatewayClientDefaults defaults,
        GatewayOptions serverGateway,
        CancellationToken ct)
    {
        var configPath = GetRuntimeConfigPath(typeInfo.ChannelId);
        if (File.Exists(configPath))
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath, ct)) as JsonObject;
            var section = root?[typeInfo.OptionsSectionName];
            var instance = section?.Deserialize(typeInfo.OptionsType, JsonOptions);
            var options = instance is null
                ? DeserializeOptions(typeInfo, null)
                : OptionsToDictionary(typeInfo, instance);
            var fileGateway = root?["Gateway"]?.Deserialize<GatewayClientConnectionOptions>(JsonOptions);
            var fileCommon = root?.Deserialize<GatewayClientCommonOptions>(JsonOptions);
            var gateway = MergeGatewayOptions(defaults.Gateway, fileGateway, serverGateway);
            var enabled = ReadInstanceEnabled(typeInfo, instance) ?? entry.Enabled;
            return new ChannelConfigSnapshot(
                options,
                gateway,
                MergeCommonOptions(defaults, fileCommon),
                enabled);
        }

        if (entry.Options is { Count: > 0 })
        {
            return new ChannelConfigSnapshot(
                DeserializeOptions(typeInfo, entry.Options),
                MergeGatewayOptions(defaults.Gateway, entry.Gateway, serverGateway),
                MergeCommonOptions(defaults, null),
                entry.Enabled);
        }

        return new ChannelConfigSnapshot(
            DeserializeOptions(typeInfo, null),
            MergeGatewayOptions(defaults.Gateway, entry.Gateway, serverGateway),
            MergeCommonOptions(defaults, null),
            entry.Enabled);
    }

    private async Task MigrateLegacyConfigAsync(
        GatewayChannelTypeInfo typeInfo,
        GatewayChannelEntry entry,
        ChannelConfigSnapshot snapshot,
        CancellationToken ct)
    {
        var configPath = GetRuntimeConfigPath(typeInfo.ChannelId);
        if (!File.Exists(configPath))
        {
            await WriteChannelConfigAsync(typeInfo, new GatewayClientViewModel
            {
                ChannelId = typeInfo.ChannelId,
                Enabled = snapshot.Enabled,
                Gateway = snapshot.Gateway,
                Agent = snapshot.Agent,
                Model = snapshot.Model,
                Mode = snapshot.Mode,
                Options = snapshot.Options
            }, ct);

            _logger.LogInformation("已将 legacy Gateway Client 配置迁移到 {Path}", configPath);
        }

        var gatewayClients = _configManager.GetSection<GatewayClientsOptions>("GatewayClients");
        if (gatewayClients.Channels.TryGetValue(typeInfo.ChannelId, out var existing))
        {
            gatewayClients.Channels[typeInfo.ChannelId] = new GatewayChannelEntry
            {
                Enabled = existing.Enabled,
                PluginSpec = existing.PluginSpec
            };
            await _configManager.SaveSectionAsync("GatewayClients", gatewayClients, ConfigLevel.Project, ct);
        }
    }

    private async Task WriteChannelConfigAsync(
        GatewayChannelTypeInfo typeInfo,
        GatewayClientViewModel model,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_clientsDirectory);

        var instance = BindOptions(typeInfo, model.Options);
        SetEnabledOnInstance(typeInfo, instance, model.Enabled);

        var root = new JsonObject
        {
            [typeInfo.OptionsSectionName] = JsonSerializer.SerializeToNode(instance, JsonOptions),
            ["Gateway"] = JsonSerializer.SerializeToNode(model.Gateway, JsonOptions)
        };

        WriteCommonOptions(root, model.Agent, model.Model, model.Mode);

        var path = GetRuntimeConfigPath(model.ChannelId);
        await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private static GatewayChannelEntry CreateRegistryEntry(
        GatewayClientViewModel model,
        GatewayChannelTypeInfo typeInfo) =>
        new()
        {
            Enabled = model.Enabled,
            PluginSpec = model.PluginSpec ?? typeInfo.PluginSpec
        };

    private static bool? ReadInstanceEnabled(GatewayChannelTypeInfo typeInfo, object? instance)
    {
        if (instance is null)
            return null;

        return typeInfo.OptionsType.GetProperty("Enabled")?.GetValue(instance) as bool?;
    }

    private static void SetEnabledOnInstance(GatewayChannelTypeInfo typeInfo, object instance, bool enabled)
    {
        var enabledProperty = typeInfo.OptionsType.GetProperty("Enabled");
        if (enabledProperty is { CanWrite: true } && enabledProperty.PropertyType == typeof(bool))
            enabledProperty.SetValue(instance, enabled);
    }

    private static GatewayClientConnectionOptions MergeGatewayOptions(
        GatewayClientConnectionOptions defaults,
        GatewayClientConnectionOptions? channelOverride,
        GatewayOptions serverGateway)
    {
        var merged = new GatewayClientConnectionOptions
        {
            BaseUrl = defaults.BaseUrl,
            Transport = defaults.Transport,
            WebSocketPath = defaults.WebSocketPath,
            ApiKey = defaults.ApiKey,
            Timeout = defaults.Timeout
        };

        if (channelOverride != null)
        {
            if (!string.IsNullOrWhiteSpace(channelOverride.BaseUrl))
                merged.BaseUrl = channelOverride.BaseUrl;
            if (!string.IsNullOrWhiteSpace(channelOverride.Transport))
                merged.Transport = channelOverride.Transport;
            if (!string.IsNullOrWhiteSpace(channelOverride.WebSocketPath))
                merged.WebSocketPath = channelOverride.WebSocketPath;
            if (!string.IsNullOrWhiteSpace(channelOverride.ApiKey))
                merged.ApiKey = channelOverride.ApiKey;
            if (!string.IsNullOrWhiteSpace(channelOverride.Timeout))
                merged.Timeout = channelOverride.Timeout;
        }

        if (string.IsNullOrWhiteSpace(channelOverride?.BaseUrl))
            merged.BaseUrl = $"http://{serverGateway.BindAddress}:{serverGateway.Port}";

        return merged;
    }

    private static GatewayClientCommonOptions MergeCommonOptions(
        GatewayClientDefaults defaults,
        GatewayClientCommonOptions? channelOverride)
    {
        var merged = new GatewayClientCommonOptions
        {
            Agent = defaults.Agent,
            Model = defaults.Model,
            Mode = defaults.Mode
        };

        if (channelOverride == null)
            return merged;

        if (!string.IsNullOrWhiteSpace(channelOverride.Agent))
            merged.Agent = channelOverride.Agent;
        if (!string.IsNullOrWhiteSpace(channelOverride.Model))
            merged.Model = channelOverride.Model;
        if (!string.IsNullOrWhiteSpace(channelOverride.Mode))
            merged.Mode = channelOverride.Mode;

        return merged;
    }

    private static void WriteCommonOptions(JsonObject root, string? agent, string? model, string? mode)
    {
        if (string.IsNullOrWhiteSpace(agent))
            root.Remove("Agent");
        else
            root["Agent"] = agent;

        if (string.IsNullOrWhiteSpace(model))
            root.Remove("Model");
        else
            root["Model"] = model;

        if (string.IsNullOrWhiteSpace(mode))
            root.Remove("Mode");
        else
            root["Mode"] = mode;
    }

    private static Dictionary<string, object?> DeserializeOptionsFromSection(
        GatewayChannelTypeInfo typeInfo,
        JsonNode? sectionNode)
    {
        if (sectionNode is null)
            return DeserializeOptions(typeInfo, null);

        var instance = sectionNode.Deserialize(typeInfo.OptionsType, JsonOptions);
        return instance is null
            ? DeserializeOptions(typeInfo, null)
            : OptionsToDictionary(typeInfo, instance);
    }

    private static Dictionary<string, object?> DeserializeOptions(
        GatewayChannelTypeInfo typeInfo,
        Dictionary<string, JsonElement>? stored)
    {
        var sample = Activator.CreateInstance(typeInfo.OptionsType);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in typeInfo.Fields)
        {
            var property = typeInfo.OptionsType.GetProperty(field.Name);
            result[field.Name] = property is null || sample is null
                ? field.DefaultValue
                : property.GetValue(sample);
        }

        if (stored == null)
            return result;

        foreach (var (key, value) in stored)
        {
            var property = typeInfo.OptionsType.GetProperty(
                key,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            result[key] = property is null
                ? JsonSerializer.Deserialize<object>(value.GetRawText(), JsonOptions)
                : JsonSerializer.Deserialize(value.GetRawText(), property.PropertyType, JsonOptions);
        }

        return result;
    }

    private static Dictionary<string, object?> OptionsToDictionary(
        GatewayChannelTypeInfo typeInfo,
        object instance)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeInfo.Fields)
        {
            var property = typeInfo.OptionsType.GetProperty(field.Name);
            if (property != null)
                result[field.Name] = property.GetValue(instance);
        }

        return result;
    }

    private static object BindOptions(GatewayChannelTypeInfo typeInfo, Dictionary<string, object?> options)
    {
        var json = JsonSerializer.Serialize(options, JsonOptions);
        return JsonSerializer.Deserialize(json, typeInfo.OptionsType, JsonOptions)
               ?? throw new ValidationException("配置无法绑定到 Options 类型");
    }

    private static void ValidateOptions(GatewayChannelTypeInfo typeInfo, Dictionary<string, object?> options)
    {
        var instance = BindOptions(typeInfo, options);
        var context = new ValidationContext(instance);
        Validator.ValidateObject(instance, context, validateAllProperties: true);
    }

    private sealed record ChannelConfigSnapshot(
        Dictionary<string, object?> Options,
        GatewayClientConnectionOptions Gateway,
        GatewayClientCommonOptions Common,
        bool Enabled)
    {
        public string? Agent => Common.Agent;
        public string? Model => Common.Model;
        public string? Mode => Common.Mode;
    }
}

public sealed class GatewayClientViewModel
{
    public string ChannelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsBuiltin { get; set; }
    public string PluginSpec { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public bool Enabled { get; set; }
    public GatewayClientConnectionOptions Gateway { get; set; } = new();
    public string? Agent { get; set; }
    public string? Model { get; set; }
    public string? Mode { get; set; }
    public Dictionary<string, object?> Options { get; set; } = new();
    public IReadOnlyList<Seeing.Gateway.Configuration.ConfigFieldSchema> Fields { get; set; } = Array.Empty<Seeing.Gateway.Configuration.ConfigFieldSchema>();
    public Type? ConfigFormComponentType { get; set; }
    public string Status { get; set; } = GatewayClientStatuses.Stopped;
    public string? LastError { get; set; }
    public int? ProcessId { get; set; }
    public string ConfigFilePath { get; set; } = "";
}