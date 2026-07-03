using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>从 .seeing/seeing.json 加载 Scheduler 配置</summary>
public sealed class SchedulerOptionsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<SchedulerOptionsProvider> _logger;
    private SchedulerOptions _options = new();

    public SchedulerOptionsProvider(IWorkspaceProvider workspace, ILogger<SchedulerOptionsProvider> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public SchedulerOptions Current => _options;

    /// <summary>从用户级 + 项目级 seeing.json 重载配置</summary>
    public void Reload()
    {
        SchedulerOptions? userOptions = null;
        SchedulerOptions? projectOptions = null;

        var userPath = Path.Combine(_workspace.UserSeeingDirectory, "seeing.json");
        var projectPath = Path.Combine(_workspace.ProjectSeeingDirectory, "seeing.json");

        userOptions = TryLoadFromFile(userPath);
        projectOptions = TryLoadFromFile(projectPath);

        var merged = userOptions ?? new SchedulerOptions();
        if (projectOptions != null)
            merged = MergeDeep.Merge(merged, projectOptions);

        _options = merged;
        _logger.LogDebug("Scheduler options reloaded (Enabled={Enabled}, Heartbeat={HeartbeatEnabled})",
            _options.Enabled, _options.Heartbeat.Enabled);
    }

    private SchedulerOptions? TryLoadFromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("SeeingAgent", out var seeingAgent))
                return ExtractScheduler(seeingAgent);

            return ExtractScheduler(root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load scheduler config from {Path}", path);
            return null;
        }
    }

    private static SchedulerOptions? ExtractScheduler(JsonElement element)
    {
        if (!element.TryGetProperty("Scheduler", out var scheduler) ||
            scheduler.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonSerializer.Deserialize<SchedulerOptions>(scheduler.GetRawText(), JsonOptions);
    }
}
