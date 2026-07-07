using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI 配置服务 - 封装 UnifiedConfigManager，提供页面友好的 API
/// </summary>
public sealed class SeeingConfigService
{
    private readonly UnifiedConfigManager _configManager;
    private readonly IWorkspaceProvider _workspaceProvider;

    public SeeingConfigService(
        UnifiedConfigManager configManager,
        IWorkspaceProvider workspaceProvider)
    {
        _configManager = configManager;
        _workspaceProvider = workspaceProvider;
    }

    // ===== SeeingAgentOptions =====

    /// <summary>加载项目级配置（不合并用户级）</summary>
    public async Task<SeeingAgentOptions> LoadProjectOptionsAsync(CancellationToken ct = default)
    {
        var project = await _configManager.GetSeeingAgentOptionsAtLevelAsync(ConfigLevel.Project, ct);
        return project ?? _configManager.GetSection<SeeingAgentOptions>("SeeingAgent");
    }

    /// <summary>加载合并后的有效配置</summary>
    public Task<SeeingAgentOptions> LoadEffectiveOptionsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_configManager.GetSection<SeeingAgentOptions>("SeeingAgent"));
    }

    // ===== Scheduler =====

    /// <summary>获取调度器配置</summary>
    public SchedulerOptions GetSchedulerOptions()
        => _configManager.GetSection<SchedulerOptions>("Scheduler");

    /// <summary>保存调度器配置</summary>
    public async Task SaveSchedulerOptionsAsync(Action<SchedulerOptions> update, CancellationToken ct = default)
    {
        var options = GetSchedulerOptions();
        update(options);
        await _configManager.SaveSectionAsync("Scheduler", options, ConfigLevel.Project, ct);
    }

    // ===== ACP =====

    /// <summary>加载合并后的 ACP 配置</summary>
    public Task<AcpOptions> LoadEffectiveAcpAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_configManager.GetSection<AcpOptions>("Acp"));
    }

    /// <summary>获取 ACP 配置来源信息</summary>
    public AcpConfigSourceInfo GetAcpConfigSourceInfo()
    {
        var info = _configManager.GetSourceInfo("Acp");
        return new AcpConfigSourceInfo
        {
            HasUserSection = info.HasUserLevel,
            UserConfigPath = info.UserPath ?? "",
            ProjectConfigPath = info.ProjectPath
        };
    }

    /// <summary>保存 ACP 配置节</summary>
    public async Task SaveAcpSectionAsync(ConfigLevel level, AcpOptions options, CancellationToken ct = default)
    {
        await _configManager.SaveSectionAsync("Acp", options, level, ct);
    }

    // ===== 通用节操作 =====

    /// <summary>加载指定级别的配置节</summary>
    public async Task<T?> LoadLevelSectionAsync<T>(
        ConfigLevel level,
        string sectionName,
        CancellationToken ct = default) where T : class
    {
        return await _configManager.GetSectionAtLevelAsync<T>(sectionName, level, ct);
    }

    /// <summary>保存配置节到项目级</summary>
    public async Task SaveProjectSectionAsync<T>(
        string sectionName,
        T value,
        CancellationToken ct = default) where T : class
    {
        await _configManager.SaveSectionAsync(sectionName, value, ConfigLevel.Project, ct);
    }

    /// <summary>批量保存配置节到指定级别</summary>
    public async Task SaveLevelSectionsAsync(
        ConfigLevel level,
        Dictionary<string, object?> sections,
        CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in sections)
        {
            if (value != null)
                dict[key] = value;
        }
        
        await _configManager.SaveSectionsAsync(level, dict, ct);
    }

    // ===== 原始 JSON =====

    /// <summary>获取项目级 seeing.json 原始 JSON</summary>
    public async Task<string> GetRawProjectJsonAsync(CancellationToken ct = default)
    {
        return await _configManager.GetRawJsonAsync(ConfigLevel.Project, "seeing.json", ct);
    }

    /// <summary>保存项目级 seeing.json 原始 JSON</summary>
    public async Task SaveRawProjectJsonAsync(string json, CancellationToken ct = default)
    {
        await _configManager.SaveRawJsonAsync(ConfigLevel.Project, "seeing.json", json, ct);
    }

    // ===== 路径信息 =====

    /// <summary>获取配置文件路径</summary>
    public string GetConfigPath(ConfigLevel level)
    {
        return level == ConfigLevel.User
            ? Path.Combine(_workspaceProvider.UserSeeingDirectory, "seeing.json")
            : Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "seeing.json");
    }
}

/// <summary>ACP 配置来源信息</summary>
public sealed class AcpConfigSourceInfo
{
    public bool HasUserSection { get; init; }
    public string UserConfigPath { get; init; } = "";
    public string ProjectConfigPath { get; init; } = "";
}
