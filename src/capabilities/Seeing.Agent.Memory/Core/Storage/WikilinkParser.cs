using System.Text.RegularExpressions;

namespace Seeing.Agent.Memory.Core.Storage;

/// <summary>
/// Wikilink 解析器 - 从 Markdown 内容中提取 [[wikilink]] 链接
/// </summary>
public static class WikilinkParser
{
    // 匹配 [[path]] 或 [[path#anchor]] 格式
    private static readonly Regex WikilinkPattern = new(
        @"\[\[([^\]\|#]+)(?:[|#][^\]]*)?\]\]",
        RegexOptions.Compiled
    );
    
    /// <summary>
    /// 从内容中解析所有 Wikilink
    /// </summary>
    public static IReadOnlyList<string> Parse(string content)
    {
        if (string.IsNullOrEmpty(content))
            return Array.Empty<string>();
        
        var links = new List<string>();
        var matches = WikilinkPattern.Matches(content);
        
        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count > 1)
            {
                var path = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(path))
                {
                    links.Add(path);
                }
            }
        }
        
        return links.Distinct().ToList();
    }
}
