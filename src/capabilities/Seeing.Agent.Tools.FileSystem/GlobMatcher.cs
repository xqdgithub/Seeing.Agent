using System.Text;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// glob 语义的唯一实现，供托管回退（<see cref="FileSystemHelper"/>）与 ripgrep 参数归一化共用。
/// <para>
/// 语义对齐 rg 的 globset：
/// <list type="bullet">
/// <item><c>**</c> 跨目录；<c>*</c> 不跨目录；<c>?</c> 单字符不跨分隔符。</item>
/// <item><c>{a,b}</c> 花括号展开；<c>[abc]</c> 字符类透传。</item>
/// <item><c>\</c> 一律视为路径分隔符，<b>不作转义</b>（见设计文档 §4.2 的取舍）。</item>
/// <item>模式含 <c>/</c> 匹配「相对搜索根的路径」，不含 <c>/</c> 匹配文件名。</item>
/// </list>
/// </para>
/// </summary>
internal static class GlobMatcher
{
    /// <summary>regex 缓存上限，避免模式数量无界增长。</summary>
    private const int MaxCacheEntries = 256;

    private static readonly Dictionary<CacheKey, Regex> s_cache = new();
    private static readonly Lock s_cacheLock = new();

    /// <summary>
    /// 归一化 glob 模式：<c>\</c> → <c>/</c>，去掉前导 <c>./</c>。
    /// <para>rg 内部一律按 <c>/</c> 处理模式（实测 <c>src\**\*.md</c> 在 Windows 上无匹配）。</para>
    /// </summary>
    public static string NormalizePattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        var normalized = pattern.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        return normalized;
    }

    /// <summary>
    /// 判断路径是否匹配 glob 模式。
    /// </summary>
    /// <param name="relativePathOrName">
    /// 待匹配对象。模式含 <c>/</c> 时应传「相对搜索根的路径」；不含 <c>/</c> 时应传文件名。
    /// </param>
    /// <param name="pattern">glob 模式（任意分隔符写法）。</param>
    /// <param name="caseSensitive">是否大小写敏感；由 <c>PathBoundary.PathComparison</c> 派生。</param>
    public static bool Matches(string relativePathOrName, string pattern, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        var normalizedPattern = NormalizePattern(pattern);
        if (normalizedPattern.Length == 0)
            return false;

        var candidate = relativePathOrName.Replace('\\', '/');
        var regex = GetOrCreateRegex(normalizedPattern, caseSensitive);
        return regex.IsMatch(candidate);
    }

    /// <summary>
    /// 判断模式是否需要「相对路径」而非「文件名」作为匹配对象。
    /// <para>含 <c>/</c> 或含 <c>**</c> 时需要相对路径。</para>
    /// </summary>
    public static bool RequiresRelativePath(string pattern)
        => NormalizePattern(pattern).Contains('/');

    /// <summary>清空缓存（仅测试用）。</summary>
    internal static void ClearCacheForTests()
    {
        lock (s_cacheLock)
            s_cache.Clear();
    }

    private static Regex GetOrCreateRegex(string normalizedPattern, bool caseSensitive)
    {
        var key = new CacheKey(normalizedPattern, caseSensitive);

        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive)
            options |= RegexOptions.IgnoreCase;

        var regex = new Regex(ConvertToRegex(normalizedPattern), options, FileSystemHelper.RegexTimeout);

        lock (s_cacheLock)
        {
            // 超出上限时整体清空：模式通常稳定，简单策略即可，避免复杂 LRU
            if (s_cache.Count >= MaxCacheEntries)
                s_cache.Clear();

            s_cache[key] = regex;
        }

        return regex;
    }

    /// <summary>
    /// 把 glob 模式转换为锚定正则。
    /// <para>
    /// <c>**/</c> 转为可选的 <c>(?:.*/)?</c>，因此 <c>**/*.md</c> 与 <c>*.md</c> 等价；
    /// 单独 <c>**</c> 转为 <c>.*</c>。
    /// </para>
    /// </summary>
    internal static string ConvertToRegex(string normalizedPattern)
    {
        var regex = new StringBuilder("^");
        var i = 0;

        while (i < normalizedPattern.Length)
        {
            var c = normalizedPattern[i];

            switch (c)
            {
                case '*':
                    if (i + 1 < normalizedPattern.Length && normalizedPattern[i + 1] == '*')
                    {
                        // ** 段：吞掉紧随其后的 '/'，使 **/ 可省略
                        i += 2;
                        if (i < normalizedPattern.Length && normalizedPattern[i] == '/')
                        {
                            regex.Append("(?:.*/)?");
                            i++;
                        }
                        else
                        {
                            regex.Append(".*");
                        }
                    }
                    else
                    {
                        regex.Append("[^/]*");
                        i++;
                    }
                    break;

                case '?':
                    regex.Append("[^/]");
                    i++;
                    break;

                case '[':
                    i = AppendCharacterClass(normalizedPattern, i, regex);
                    break;

                case '{':
                    i = AppendBraceGroup(normalizedPattern, i, regex);
                    break;

                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        regex.Append('$');
        return regex.ToString();
    }

    /// <summary>追加字符类；未闭合时按字面量处理。</summary>
    private static int AppendCharacterClass(string pattern, int start, StringBuilder regex)
    {
        var end = pattern.IndexOf(']', start + 1);
        if (end < 0)
        {
            regex.Append(Regex.Escape("["));
            return start + 1;
        }

        var body = pattern[(start + 1)..end];
        if (body.Length == 0)
        {
            regex.Append(Regex.Escape("[]"));
            return end + 1;
        }

        // globset 用 ! 表示取反，正则用 ^
        if (body[0] == '!')
            body = "^" + body[1..];

        regex.Append('[').Append(body).Append(']');
        return end + 1;
    }

    /// <summary>追加花括号展开组；未闭合时按字面量处理。</summary>
    private static int AppendBraceGroup(string pattern, int start, StringBuilder regex)
    {
        var end = pattern.IndexOf('}', start + 1);
        if (end < 0)
        {
            regex.Append(Regex.Escape("{"));
            return start + 1;
        }

        var body = pattern[(start + 1)..end];
        if (body.Length == 0)
        {
            regex.Append(Regex.Escape("{}"));
            return end + 1;
        }

        // 每个候选仍按 glob 语法解释（支持 {src,test}/**）
        var alternatives = body.Split(',')
            .Select(alt => ConvertToRegexBody(NormalizePattern(alt)))
            .ToArray();

        regex.Append("(?:").Append(string.Join('|', alternatives)).Append(')');
        return end + 1;
    }

    /// <summary>把片段（非整模式）转换为正则体，供花括号候选使用。</summary>
    private static string ConvertToRegexBody(string pattern)
    {
        var anchored = ConvertToRegex(pattern);
        return anchored[1..^1];
    }

    private readonly record struct CacheKey(string Pattern, bool CaseSensitive);
}
