using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 系统提示词提供者 - 根据 Provider/Model 选择模板
/// </summary>
public class SystemPromptProvider
{
    private readonly ILogger<SystemPromptProvider> _logger;
    private readonly Dictionary<string, string> _templates = new();
    private readonly Dictionary<string, string> _customTemplates = new();

    public SystemPromptProvider(ILogger<SystemPromptProvider> logger)
    {
        _logger = logger;
        LoadEmbeddedTemplates();
    }

    private void LoadEmbeddedTemplates()
    {
        var assembly = typeof(SystemPromptProvider).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Prompts.Templates") && n.EndsWith(".txt"));

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            // 从资源名提取模板名称（如 "Seeing.Agent.Core.Prompts.Templates.default.txt" → "default"）
            var parts = name.Split('.');
            var templateName = parts.Length >= 2 ? parts[^2] : name;
            _templates[templateName] = content;
        }
    }

    /// <summary>
    /// 获取 Provider 特定的系统提示词模板
    /// </summary>
    public string GetTemplate(string providerId, string modelId)
    {
        // 检查自定义模板
        foreach (var (pattern, template) in _customTemplates)
        {
            if (MatchesPattern(modelId, pattern) || MatchesPattern(providerId, pattern))
            {
                return template;
            }
        }

        // 根据 Model ID 选择模板
        var templateName = SelectTemplateName(modelId);

        return _templates.TryGetValue(templateName, out var content)
            ? content
            : _templates.GetValueOrDefault("default", string.Empty);
    }

    /// <summary>
    /// 注册自定义模板
    /// </summary>
    public void RegisterTemplate(string pattern, string template)
    {
        _customTemplates[pattern] = template;
    }

    private static string SelectTemplateName(string modelId)
    {
        var lower = modelId.ToLowerInvariant();

        if (lower.Contains("gpt-4") || lower.Contains("o1") || lower.Contains("o3"))
            return "beast";
        if (lower.Contains("gpt"))
            return "gpt";
        if (lower.Contains("gemini-"))
            return "gemini";
        if (lower.Contains("claude"))
            return "anthropic";

        return "default";
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
            return value.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith("*"))
            return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith("*"))
            return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
