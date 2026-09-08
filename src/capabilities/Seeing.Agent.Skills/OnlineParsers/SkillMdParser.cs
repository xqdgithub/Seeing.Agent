using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Skills.OnlineParsers;

/// <summary>
/// SKILL.md 格式解析帮助类
/// </summary>
public static class SkillMdParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// 解析 SKILL.md 内容
    /// </summary>
    public static OnlineSkillResult? Parse(string content, string sourceUrl)
    {
        if (string.IsNullOrEmpty(content) || !content.StartsWith("---"))
            return null;

        var endIndex = content.IndexOf("\n---", 4);
        if (endIndex < 0)
            return null;

        var frontmatter = content.Substring(4, endIndex - 4);
        var bodyStart = endIndex + 4;

        try
        {
            var yaml = YamlDeserializer.Deserialize<Dictionary<string, object?>>(frontmatter);
            if (yaml == null)
                return null;

            var name = yaml.TryGetValue("name", out var nameObj) ? nameObj?.ToString() : null;
            if (string.IsNullOrEmpty(name))
                return null;

            var result = new OnlineSkillResult
            {
                Name = name,
                Description = yaml.TryGetValue("description", out var descObj) ? descObj?.ToString() ?? "" : "",
                Author = yaml.TryGetValue("author", out var authorObj) ? authorObj?.ToString() : null,
                Version = yaml.TryGetValue("version", out var versionObj) ? versionObj?.ToString() : null,
                Content = content,
                SourceUrl = sourceUrl
            };

            // 解析 tags
            if (yaml.TryGetValue("tags", out var tagsObj))
            {
                var tagsStr = tagsObj?.ToString() ?? "";
                if (!string.IsNullOrEmpty(tagsStr))
                {
                    result.Tags = tagsStr.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }
            }

            return result;
        }
        catch
        {
            // YAML 解析失败，尝试简单解析
            return ParseSimple(content, sourceUrl);
        }
    }

    /// <summary>
    /// 简单解析（正则方式）
    /// </summary>
    private static OnlineSkillResult? ParseSimple(string content, string sourceUrl)
    {
        var nameMatch = Regex.Match(content, @"^name:\s*(.+)$", RegexOptions.Multiline);
        if (!nameMatch.Success)
            return null;

        var result = new OnlineSkillResult
        {
            Name = nameMatch.Groups[1].Value.Trim(),
            Content = content,
            SourceUrl = sourceUrl
        };

        var descMatch = Regex.Match(content, @"^description:\s*(.+)$", RegexOptions.Multiline);
        if (descMatch.Success)
            result.Description = descMatch.Groups[1].Value.Trim();

        var authorMatch = Regex.Match(content, @"^author:\s*(.+)$", RegexOptions.Multiline);
        if (authorMatch.Success)
            result.Author = authorMatch.Groups[1].Value.Trim();

        var versionMatch = Regex.Match(content, @"^version:\s*(.+)$", RegexOptions.Multiline);
        if (versionMatch.Success)
            result.Version = versionMatch.Groups[1].Value.Trim();

        return result;
    }
}
