namespace Seeing.Agent.WebUI.Services;

using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// Skill 状态服务 - 管理 Skill 启用/禁用状态
/// </summary>
public sealed class SkillStateService
{
    private readonly ILogger<SkillStateService> _logger;
    private readonly string _configFilePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HashSet<string> _disabledSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>状态变更事件（UI 组件订阅）</summary>
    public event Action? OnStateChanged;

    public SkillStateService(ILogger<SkillStateService> logger)
    {
        _logger = logger;

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var seeingDirectory = Path.Combine(userProfilePath, ".seeing");

        // 确保目录存在
        if (!Directory.Exists(seeingDirectory))
        {
            Directory.CreateDirectory(seeingDirectory);
            _logger.LogDebug("创建配置目录: {Directory}", seeingDirectory);
        }

        _configFilePath = Path.Combine(seeingDirectory, "skill-state.json");

        _logger.LogInformation("SkillStateService 已初始化，配置文件: {FilePath}", _configFilePath);
    }

    /// <summary>检查 Skill 是否启用</summary>
    /// <param name="skillName">Skill 名称</param>
    /// <returns>true 表示启用，false 表示禁用</returns>
    public bool IsEnabled(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            _logger.LogWarning("Skill 名称不能为空");
            return true;
        }

        lock (_lock)
        {
            return !_disabledSkills.Contains(skillName);
        }
    }

    /// <summary>设置 Skill 启用/禁用状态</summary>
    /// <param name="skillName">Skill 名称</param>
    /// <param name="enabled">true 表示启用，false 表示禁用</param>
    public void SetEnabled(string skillName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            _logger.LogWarning("Skill 名称不能为空");
            return;
        }

        bool changed = false;

        lock (_lock)
        {
            if (enabled)
            {
                changed = _disabledSkills.Remove(skillName);
                if (changed)
                {
                    _logger.LogDebug("Skill 已启用: {SkillName}", skillName);
                }
            }
            else
            {
                changed = _disabledSkills.Add(skillName);
                if (changed)
                {
                    _logger.LogDebug("Skill 已禁用: {SkillName}", skillName);
                }
            }
        }

        if (changed)
        {
            // 异步保存，不阻塞当前操作
            _ = SaveAsync();
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>获取所有禁用的 Skill</summary>
    /// <returns>禁用 Skill 名称集合</returns>
    public IReadOnlySet<string> GetDisabledSkills()
    {
        lock (_lock)
        {
            return new HashSet<string>(_disabledSkills, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>从文件加载状态</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogDebug("配置文件不存在，使用空状态: {FilePath}", _configFilePath);
                return;
            }

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogDebug("配置文件为空");
                return;
            }

            var data = JsonSerializer.Deserialize<SkillStateData>(json, _jsonOptions);

            if (data?.DisabledSkills is not null)
            {
                lock (_lock)
                {
                    _disabledSkills.Clear();
                    foreach (var skill in data.DisabledSkills)
                    {
                        _disabledSkills.Add(skill);
                    }
                }

                _logger.LogInformation("已加载 {Count} 个禁用 Skill", data.DisabledSkills.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 Skill 状态失败: {FilePath}", _configFilePath);
            // 加载失败时保持空状态，不抛出异常
        }
    }

    /// <summary>保存状态到文件</summary>
    public async Task SaveAsync()
    {
        try
        {
            List<string> disabledList;

            lock (_lock)
            {
                disabledList = _disabledSkills.ToList();
            }

            var data = new SkillStateData
            {
                DisabledSkills = disabledList
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);

            await File.WriteAllTextAsync(_configFilePath, json);

            _logger.LogDebug("已保存 Skill 状态: {Count} 个禁用", disabledList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Skill 状态失败: {FilePath}", _configFilePath);
            // 保存失败不抛出异常，避免影响主流程
        }
    }

    /// <summary>内部数据结构</summary>
    private sealed class SkillStateData
    {
        public List<string>? DisabledSkills { get; set; }
    }
}
