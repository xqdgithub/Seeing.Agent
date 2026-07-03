using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 集中管理 <see cref="SeeingAgentOptions"/> 的加载与热更新（用户级 + 项目级 .seeing/seeing.json）。
/// </summary>
public sealed class SeeingAgentConfigurationProvider
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly object _lock = new();

    public SeeingAgentOptions Options { get; } = new()
    {
        Models = new Dictionary<string, ModelConfig>(),
        Providers = new Dictionary<string, ProviderConfig>(),
        Agents = new Dictionary<string, AgentConfig>()
    };

    /// <summary>加载用户级与项目级配置（项目级目录由 workspaceRoot 决定）</summary>
    public void Load(string? workspaceRoot = null, ILogger? logger = null)
    {
        lock (_lock)
        {
            ResetOptionsInternal();
            LoadFiles(workspaceRoot, logger);
        }
    }

    /// <summary>在 InitializeSeeingAgentAsync 时按工作区根目录重载配置</summary>
    public void ReloadForWorkspace(string workspaceRoot, ILogger? logger = null)
    {
        lock (_lock)
        {
            ResetOptionsInternal();
            LoadFiles(workspaceRoot, logger);
            logger?.LogInformation("已按工作区 {WorkspaceRoot} 重载 SeeingAgent 配置", workspaceRoot);
        }
    }

    private void ResetOptionsInternal()
    {
        Options.DefaultProvider = null;
        Options.DefaultModel = null;
        Options.DefaultAgent = null;
        Options.Providers.Clear();
        Options.Models!.Clear();
        Options.Agents.Clear();
        Options.Gateway = new GatewayOptions();
        Options.Acp = new AcpOptions();
        Options.Plugins.Clear();
        Options.PluginEnabled.Clear();
        Options.Skills = new SkillsConfig();
        Options.Permission = new PermissionOptions();
        Options.ModelScope = null;
    }

    private void LoadFiles(string? workspaceRoot, ILogger? logger)
    {
        var workspaceProvider = new WorkspaceProvider(workspaceRoot);

        var userPath = Path.Combine(workspaceProvider.UserSeeingDirectory, "seeing.json");
        LoadFromFile(userPath, Options, "用户级", logger);

        var projectPath = Path.Combine(workspaceProvider.ProjectSeeingDirectory, "seeing.json");
        LoadFromFile(projectPath, Options, "项目级", logger);
    }

    internal static void LoadFromFile(
        string path,
        SeeingAgentOptions options,
        string level,
        ILogger? logger = null)
    {
        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var root = doc.RootElement;
            if (root.TryGetProperty("SeeingAgent", out var seeingAgentSection))
                ParseOptions(seeingAgentSection, options, logger);
            else
                ParseOptions(root, options, logger);

            logger?.LogDebug("已从 {Level} 配置文件加载配置: {Path}", level, path);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger?.LogWarning(ex,
                "{Level} 配置文件格式错误，已跳过: {Path}。错误位置: {Message}",
                level, path, ex.Message);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "加载 {Level} 配置文件失败，已跳过: {Path}", level, path);
        }
    }

    internal static void ParseOptions(
        System.Text.Json.JsonElement element,
        SeeingAgentOptions options,
        ILogger? logger = null)
    {
        if (element.TryGetProperty("Providers", out var providers) &&
            providers.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in providers.EnumerateObject())
            {
                try
                {
                    var providerConfig = System.Text.Json.JsonSerializer.Deserialize<ProviderConfig>(
                        prop.Value.GetRawText(), JsonOptions);
                    if (providerConfig != null)
                        options.Providers[prop.Name] = providerConfig;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "解析 Providers.{Name} 失败，已跳过", prop.Name);
                }
            }
        }

        if (element.TryGetProperty("Models", out var models) &&
            models.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in models.EnumerateObject())
            {
                try
                {
                    var modelConfig = System.Text.Json.JsonSerializer.Deserialize<ModelConfig>(
                        prop.Value.GetRawText(), JsonOptions);
                    if (modelConfig != null)
                        options.Models![prop.Name] = modelConfig;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "解析 Models.{Name} 失败，已跳过", prop.Name);
                }
            }
        }

        if (element.TryGetProperty("DefaultProvider", out var defaultProvider) &&
            defaultProvider.ValueKind == System.Text.Json.JsonValueKind.String)
            options.DefaultProvider = defaultProvider.GetString();

        if (element.TryGetProperty("DefaultModel", out var defaultModel) &&
            defaultModel.ValueKind == System.Text.Json.JsonValueKind.String)
            options.DefaultModel = defaultModel.GetString();

        if (element.TryGetProperty("DefaultAgent", out var defaultAgent) &&
            defaultAgent.ValueKind == System.Text.Json.JsonValueKind.String)
            options.DefaultAgent = defaultAgent.GetString();

        if (element.TryGetProperty("Agents", out var agents) &&
            agents.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in agents.EnumerateObject())
            {
                try
                {
                    var agentConfig = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(
                        prop.Value.GetRawText(), JsonOptions);
                    if (agentConfig != null)
                        options.Agents[prop.Name] = agentConfig;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "解析 Agents.{Name} 失败，已跳过", prop.Name);
                }
            }
        }

        if (element.TryGetProperty("Gateway", out var gateway) &&
            gateway.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            try
            {
                LegacyGatewayConfigMigrator.Apply(gateway, options, logger);

                var gatewayOptions = System.Text.Json.JsonSerializer.Deserialize<GatewayOptions>(
                    gateway.GetRawText(), JsonOptions);
                if (gatewayOptions != null)
                    options.Gateway = MergeDeep.Merge(options.Gateway, gatewayOptions);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "解析 Gateway 配置失败，已跳过");
            }
        }

        if (element.TryGetProperty("Acp", out var acp) &&
            acp.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            try
            {
                var acpOptions = System.Text.Json.JsonSerializer.Deserialize<AcpOptions>(
                    acp.GetRawText(), JsonOptions);
                if (acpOptions != null)
                    options.Acp = MergeDeep.Merge(options.Acp, acpOptions);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "解析 Acp 配置失败，已跳过");
            }
        }

        if (element.TryGetProperty("Plugins", out var plugins) &&
            plugins.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            options.Plugins = ParsePluginsFromJson(plugins, logger);
        }
    }

    private static List<PluginSpec> ParsePluginsFromJson(
        System.Text.Json.JsonElement plugins,
        ILogger? logger)
    {
        var result = new List<PluginSpec>();

        foreach (var item in plugins.EnumerateArray())
        {
            try
            {
                switch (item.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.String:
                        result.Add(new PluginSpec { Spec = item.GetString() ?? "" });
                        break;
                    case System.Text.Json.JsonValueKind.Object:
                        var spec = System.Text.Json.JsonSerializer.Deserialize<PluginSpec>(
                            item.GetRawText(), JsonOptions);
                        if (spec != null && !string.IsNullOrEmpty(spec.Spec))
                            result.Add(spec);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "解析 Plugins 条目失败，已跳过");
            }
        }

        return result;
    }
}

/// <summary>将可变配置暴露为 <see cref="IOptions{TOptions}"/>。</summary>
public sealed class SeeingAgentOptionsMonitor : IOptions<SeeingAgentOptions>
{
    private readonly SeeingAgentConfigurationProvider _provider;

    public SeeingAgentOptionsMonitor(SeeingAgentConfigurationProvider provider) =>
        _provider = provider;

    public SeeingAgentOptions Value => _provider.Options;
}

/// <summary>将 Gateway 配置暴露为 <see cref="IOptions{TOptions}"/>。</summary>
public sealed class GatewayOptionsMonitor : IOptions<GatewayOptions>
{
    private readonly SeeingAgentConfigurationProvider _provider;

    public GatewayOptionsMonitor(SeeingAgentConfigurationProvider provider) =>
        _provider = provider;

    public GatewayOptions Value => _provider.Options.Gateway;
}
