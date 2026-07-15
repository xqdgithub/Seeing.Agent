using Seeing.Agent.Configuration;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Agent MD 配置服务（UI 层封装）
/// </summary>
public class AgentMdConfigService
{
    private readonly IAgentConfigLoader _loader;

    public AgentMdConfigService(IAgentConfigLoader loader)
    {
        _loader = loader;
    }

    /// <summary>
    /// 获取所有 MD 配置信息
    /// </summary>
    public Task<IReadOnlyList<AgentMdInfo>> GetAllAsync(CancellationToken ct = default)
        => _loader.GetAllWithLevelAsync(ct);

    /// <summary>
    /// 获取单个 MD 配置内容
    /// </summary>
    public async Task<string?> GetContentAsync(string name, ConfigLevel level, CancellationToken ct = default)
    {
        var filePath = _loader.GetFilePath(name, level);
        if (!File.Exists(filePath)) return null;
        return await File.ReadAllTextAsync(filePath, ct);
    }

    /// <summary>
    /// 创建新的 MD 配置
    /// </summary>
    public Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, CancellationToken ct = default)
        => _loader.CreateAsync(name, level, null, ct);

    /// <summary>
    /// 保存 MD 配置
    /// </summary>
    public Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default)
        => _loader.SaveAsync(name, level, content, ct);

    /// <summary>
    /// 删除 MD 配置
    /// </summary>
    public Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default)
        => _loader.DeleteAsync(name, level, ct);

    /// <summary>
    /// 获取默认模板
    /// </summary>
    public string GetDefaultTemplate(string agentName)
        => AgentConfigLoader.GetDefaultTemplate(agentName);

    /// <summary>
    /// 验证内容格式
    /// </summary>
    public ValidationResult ValidateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ValidationResult.Failure("内容不能为空");

        // 检查 YAML Front Matter
        if (!content.StartsWith("---"))
            return ValidationResult.Failure("缺少 YAML Front Matter（应以 --- 开头）");

        var frontMatterEnd = content.IndexOf("---", 3);
        if (frontMatterEnd == -1)
            return ValidationResult.Failure("YAML Front Matter 格式错误（缺少结束 ---）");

        var yamlContent = content.Substring(3, frontMatterEnd - 3).Trim();
        if (string.IsNullOrWhiteSpace(yamlContent))
            return ValidationResult.Failure("YAML Front Matter 内容为空");

        // 检查 name 字段
        if (!yamlContent.Contains("name:"))
            return ValidationResult.Failure("YAML Front Matter 缺少 name 字段");

        return ValidationResult.Success();
    }

    /// <summary>
    /// 获取文件路径
    /// </summary>
    public string GetFilePath(string name, ConfigLevel level)
        => _loader.GetFilePath(name, level);
}

/// <summary>
/// 验证结果
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; }
    public string? Error { get; }

    private ValidationResult(bool isValid, string? error = null)
    {
        IsValid = isValid;
        Error = error;
    }

    public static ValidationResult Success() => new(true);
    public static ValidationResult Failure(string error) => new(false, error);
}
