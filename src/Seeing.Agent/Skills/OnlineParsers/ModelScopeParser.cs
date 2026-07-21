using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Skills.OnlineParsers;

/// <summary>
/// ModelScope 技能市场解析器
/// <para>
/// 支持 URL: https://modelscope.cn/skills/{owner}/{skill-name}
/// 支持 ZIP 下载：https://modelscope.cn/skills/{owner}/{skill-name}/archive/zip/master.zip
/// </para>
/// </summary>
public class ModelScopeParser : IOnlineSkillParser
{
    public string Name => "ModelScope";

    public bool CanParse(string url)
    {
        return url.Contains("modelscope.cn") && url.Contains("/skills/");
    }

    public async Task<OnlineSkillResult?> ParseAsync(string url, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        // 从 URL 提取 owner 和 skill name
        // URL 格式: https://modelscope.cn/skills/{owner}/{skill-name}
        var match = Regex.Match(url, @"/skills/([^/]+)/([^/?]+)");
        if (!match.Success)
            return null;

        var owner = match.Groups[1].Value;
        var skillName = match.Groups[2].Value;

        // 调用 API
        var apiUrl = $"https://modelscope.cn/api/v1/skills/{owner}/{skillName}";
        
        try
        {
            var response = await httpClient.GetAsync(apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var jsonDoc = JsonDocument.Parse(jsonContent);

            if (!jsonDoc.RootElement.TryGetProperty("Data", out var data))
                return null;

            // 获取 SKILL.md 内容
            if (!data.TryGetProperty("ReadMeContent", out var readmeProp))
                return null;

            var skillContent = readmeProp.GetString();
            if (string.IsNullOrEmpty(skillContent))
                return null;

            // 解析 SKILL.md
            var result = SkillMdParser.Parse(skillContent, url);
            if (result == null)
                return null;

            // 获取 owner 和 skill name（从 API 返回，更准确）
            var path = data.TryGetProperty("Path", out var pathProp) ? pathProp.GetString() : owner;
            var name = data.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : skillName;

            // 构造 ZIP 下载链接
            // 格式: https://modelscope.cn/skills/{path}/{name}/archive/zip/master.zip
            result.DownloadUrl = $"https://modelscope.cn/skills/{path}/{name}/archive/zip/master.zip";

            // 获取源仓库信息（如果有）
            if (data.TryGetProperty("SourceURL", out var sourceUrlProp))
            {
                result.RepositoryUrl = sourceUrlProp.GetString();
            }

            // 补充元数据（如果 SKILL.md 中没有）
            if (string.IsNullOrEmpty(result.Description) && data.TryGetProperty("Description", out var descProp))
            {
                result.Description = descProp.GetString() ?? "";
            }

            // 获取标签
            if (data.TryGetProperty("Tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                result.Tags ??= new List<string>();
                foreach (var tag in tagsProp.EnumerateArray())
                {
                    var tagStr = tag.GetString();
                    if (!string.IsNullOrEmpty(tagStr))
                        result.Tags.Add(tagStr);
                }
            }

            return result;
        }
        catch (Exception)
        {
            return null;
        }
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

