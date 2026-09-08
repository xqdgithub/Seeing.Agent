namespace Seeing.Agent.Skills.OnlineParsers;

/// <summary>
/// 在线技能解析器接口
/// <para>
/// 支持从不同网站解析技能内容，每个网站实现一个解析器。
/// </para>
/// </summary>
public interface IOnlineSkillParser
{
    /// <summary>
    /// 解析器名称（用于日志和显示）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 判断是否能解析该 URL
    /// </summary>
    /// <param name="url">技能页面 URL</param>
    /// <returns>是否能解析</returns>
    bool CanParse(string url);

    /// <summary>
    /// 解析技能元数据（包括下载链接）
    /// </summary>
    /// <param name="url">技能页面 URL</param>
    /// <param name="httpClient">HTTP 客户端</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>解析结果，失败返回 null</returns>
    Task<OnlineSkillResult?> ParseAsync(string url, HttpClient httpClient, CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载技能 ZIP 包
    /// </summary>
    /// <param name="result">解析结果（包含 DownloadUrl）</param>
    /// <param name="httpClient">HTTP 客户端</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>ZIP 文件字节数组，失败返回 null</returns>
    Task<byte[]?> DownloadZipAsync(OnlineSkillResult result, HttpClient httpClient, CancellationToken cancellationToken = default);
}
