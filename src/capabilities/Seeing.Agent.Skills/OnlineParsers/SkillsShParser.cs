using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Skills.OnlineParsers;

/// <summary>
/// skills.sh 技能市场解析器
/// <para>
/// 支持 URL: https://www.skills.sh/{owner}/skills/{skill-name}
/// 从页面解析 GitHub 仓库地址，从 GitHub 下载完整技能
/// </para>
/// </summary>
public class SkillsShParser : IOnlineSkillParser
{
    public string Name => "Skills.sh";

    public bool CanParse(string url)
    {
        return url.Contains("skills.sh");
    }

    public async Task<OnlineSkillResult?> ParseAsync(string url, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        // 从 URL 提取 owner 和 skill name
        // URL 格式: https://www.skills.sh/{owner}/skills/{skill-name}
        var match = Regex.Match(url, @"skills\.sh/([^/]+)/skills/([^/?]+)");
        if (!match.Success)
            return null;

        var owner = match.Groups[1].Value;
        var skillName = match.Groups[2].Value;

        // 从页面解析 GitHub 仓库地址
        string repoOwner = owner;
        string repoName = "skills";
        string branch = "main";

        try
        {
            var pageResponse = await httpClient.GetAsync(url, cancellationToken);
            if (pageResponse.IsSuccessStatusCode)
            {
                var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
                
                // 从页面提取安装命令中的 GitHub URL
                // 格式: npx skills add https://github.com/owner/repo --skill name
                // 或: npx skills add https://github.com/owner/repo/tree/branch --skill name
                var commandMatch = Regex.Match(html, @"npx skills add (https://github\.com/([^/\s""]+)/([^/\s""]+))(?:/tree/([^/\s""]+))?");
                if (commandMatch.Success)
                {
                    repoOwner = commandMatch.Groups[2].Value;
                    repoName = commandMatch.Groups[3].Value;
                    if (commandMatch.Groups[4].Success && !string.IsNullOrEmpty(commandMatch.Groups[4].Value))
                    {
                        branch = commandMatch.Groups[4].Value;
                    }
                }
            }
        }
        catch
        {
            // 忽略，使用默认值
        }

        // 使用 GitHub API 获取仓库文件树，找到技能目录
        string? skillPath = null;
        string? skillContent = null;

        try
        {
            // 获取仓库文件树
            var treeUrl = $"https://api.github.com/repos/{repoOwner}/{repoName}/git/trees/{branch}?recursive=1";
            var request = new HttpRequestMessage(HttpMethod.Get, treeUrl);
            request.Headers.UserAgent.TryParseAdd("Seeing.Agent");
            
            var treeResponse = await httpClient.SendAsync(request, cancellationToken);
            if (treeResponse.IsSuccessStatusCode)
            {
                var treeContent = await treeResponse.Content.ReadAsStringAsync(cancellationToken);
                
                // 解析 JSON
                using var doc = JsonDocument.Parse(treeContent);
                if (doc.RootElement.TryGetProperty("tree", out var tree))
                {
                    // 第一遍：找到包含 SKILL.md 且路径匹配技能名的目录
                    foreach (var item in tree.EnumerateArray())
                    {
                        var type = item.GetProperty("type").GetString();
                        if (type != "blob") continue;
                        
                        var path = item.GetProperty("path").GetString() ?? "";
                        
                        // 检查是否是 SKILL.md 且路径包含技能名
                        if (path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                            path.Contains($"/{skillName}/", StringComparison.OrdinalIgnoreCase))
                        {
                            skillPath = Path.GetDirectoryName(path)?.Replace('\\', '/');
                            break;
                        }
                    }
                    
                    // 如果没找到，尝试其他匹配方式
                    if (string.IsNullOrEmpty(skillPath))
                    {
                        foreach (var item in tree.EnumerateArray())
                        {
                            var type = item.GetProperty("type").GetString();
                            if (type != "blob") continue;
                            
                            var path = item.GetProperty("path").GetString() ?? "";
                            
                            if (path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
                            {
                                // 检查路径是否以技能名结尾
                                var dirPath = Path.GetDirectoryName(path)?.Replace('\\', '/');
                                if (!string.IsNullOrEmpty(dirPath))
                                {
                                    var dirName = Path.GetFileName(dirPath);
                                    if (dirName.Equals(skillName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        skillPath = dirPath;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GitHub API error: {ex.Message}");
        }

        // 如果找到了路径，获取 SKILL.md 内容
        if (!string.IsNullOrEmpty(skillPath))
        {
            var rawUrl = $"https://raw.githubusercontent.com/{repoOwner}/{repoName}/{branch}/{skillPath}/SKILL.md";
            try
            {
                var response = await httpClient.GetAsync(rawUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    skillContent = await response.Content.ReadAsStringAsync(cancellationToken);
                }
            }
            catch
            {
                // 忽略
            }
        }

        if (string.IsNullOrEmpty(skillContent))
            return null;

        var result = SkillMdParser.Parse(skillContent, url);
        if (result == null)
            return null;

        // 设置下载信息
        result.RepositoryUrl = $"https://github.com/{repoOwner}/{repoName}";
        result.DownloadUrl = $"https://github.com/{repoOwner}/{repoName}/archive/refs/heads/{branch}.zip";
        result.Files.Add(skillPath!); // 第一个元素是技能目录路径

        return result;
    }

    public async Task<byte[]?> DownloadZipAsync(OnlineSkillResult result, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(result.DownloadUrl))
            return null;

        try
        {
            var response = await httpClient.GetAsync(result.DownloadUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
