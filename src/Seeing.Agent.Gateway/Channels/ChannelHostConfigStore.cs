using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// ChannelHost 配置存储：读取 channel 列表、管理运行时状态文件。
/// 通过 <see cref="IConfigSectionStore"/> 统一管理配置读写。
/// </summary>
public sealed class ChannelHostConfigStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IConfigSectionStore _configStore;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly GatewayChannelRegistry _registry;
    private readonly string _clientsDirectory;

    public ChannelHostConfigStore(
        IConfigSectionStore configStore,
        IWorkspaceProvider workspaceProvider,
        GatewayChannelRegistry registry)
    {
        _configStore = configStore;
        _workspaceProvider = workspaceProvider;
        _registry = registry;
        _clientsDirectory = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "gateway-clients");
    }

    public string ClientsDirectory => _clientsDirectory;

    public IReadOnlyList<ChannelHostEntry> GetChannelHosts()
    {
        var gatewayClients = _configStore.GetSection<GatewayClientsOptions>("GatewayClients");
        var serverGateway = _configStore.GetSection<GatewayOptions>("Gateway");
        var result = new List<ChannelHostEntry>();

        foreach (var typeInfo in _registry.Types)
        {
            gatewayClients.Channels.TryGetValue(typeInfo.ChannelId, out var entry);

            var enabled = entry?.Enabled ?? false;
            var gatewayBaseUrl = $"http://{serverGateway.BindAddress}:{serverGateway.Port}";

            result.Add(new ChannelHostEntry
            {
                ChannelId = typeInfo.ChannelId,
                DisplayName = typeInfo.DisplayName,
                Enabled = enabled,
                PluginSpec = entry?.PluginSpec ?? typeInfo.PluginSpec,
                AssemblyPath = typeInfo.AssemblyPath,
                ConfigFilePath = GetRuntimeConfigPath(typeInfo.ChannelId),
                GatewayBaseUrl = gatewayBaseUrl
            });
        }

        return result;
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
        return JsonSerializer.Deserialize<GatewayClientRuntimeState>(json, s_jsonOptions)
               ?? new GatewayClientRuntimeState();
    }

    public async Task SaveRuntimeStateAsync(string channelId, GatewayClientRuntimeState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_clientsDirectory);
        var path = GetRuntimeStatePath(channelId);
        var json = JsonSerializer.Serialize(state, s_jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }
}
