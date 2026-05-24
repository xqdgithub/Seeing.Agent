namespace Seeing.Agent.WebUI.Services;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// 工具状态服务 - 管理工具的启用/禁用状态
/// </summary>
public sealed class ToolStateService
{
    private readonly ILogger<ToolStateService> _logger;
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, byte> _disabledTools = new();

    /// <summary>状态变更事件（UI 组件订阅）</summary>
    public event Action? OnStateChanged;

    public ToolStateService(ILogger<ToolStateService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing",
            "tool-state.json");
    }

    /// <summary>判断工具是否启用（不在禁用集合中即为启用）</summary>
    public bool IsEnabled(string toolId) => !_disabledTools.ContainsKey(toolId);

    /// <summary>设置工具启用/禁用状态</summary>
    public void SetEnabled(string toolId, bool enabled)
    {
        if (enabled)
        {
            _disabledTools.TryRemove(toolId, out _);
            _logger.LogDebug("工具已启用: {ToolId}", toolId);
        }
        else
        {
            _disabledTools.TryAdd(toolId, 0);
            _logger.LogDebug("工具已禁用: {ToolId}", toolId);
        }

        OnStateChanged?.Invoke();
    }

    /// <summary>获取所有禁用的工具 ID</summary>
    public IReadOnlySet<string> GetDisabledTools() => _disabledTools.Keys.ToHashSet();

    /// <summary>从配置文件加载状态</summary>
    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogInformation("工具状态文件不存在，使用空状态: {Path}", _filePath);
                return;
            }

            var json = await File.ReadAllTextAsync(_filePath);
            var disabledIds = JsonSerializer.Deserialize<HashSet<string>>(json);

            if (disabledIds is not null)
            {
                foreach (var id in disabledIds)
                {
                    _disabledTools.TryAdd(id, 0);
                }
            }

            _logger.LogInformation("已加载工具状态，{Count} 个工具被禁用", _disabledTools.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载工具状态文件失败: {Path}", _filePath);
        }
    }

    /// <summary>保存状态到配置文件</summary>
    public async Task SaveAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_disabledTools.Keys.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(_filePath, json);
            _logger.LogDebug("工具状态已保存: {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存工具状态文件失败: {Path}", _filePath);
        }
    }
}
