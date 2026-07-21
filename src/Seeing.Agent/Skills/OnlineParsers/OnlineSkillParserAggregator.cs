using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Skills.OnlineParsers;

/// <summary>
/// 在线技能解析器聚合器
/// <para>
/// 管理多个解析器，根据 URL 自动选择合适的解析器。
/// </para>
/// </summary>
public class OnlineSkillParserAggregator
{
    private readonly ILogger<OnlineSkillParserAggregator> _logger;
    private readonly List<IOnlineSkillParser> _parsers = new();

    public OnlineSkillParserAggregator(ILogger<OnlineSkillParserAggregator> logger)
    {
        _logger = logger;
        
        // 注册默认解析器
        RegisterParser(new SkillsShParser());
        RegisterParser(new ModelScopeParser());
    }

    /// <summary>
    /// 注册解析器
    /// </summary>
    public void RegisterParser(IOnlineSkillParser parser)
    {
        _parsers.Add(parser);
        _logger.LogDebug("注册在线技能解析器: {ParserName}", parser.Name);
    }

    /// <summary>
    /// 获取所有已注册的解析器
    /// </summary>
    public IReadOnlyList<IOnlineSkillParser> GetParsers() => _parsers;

    /// <summary>
    /// 解析技能元数据
    /// </summary>
    /// <param name="url">技能页面 URL</param>
    /// <param name="httpClient">HTTP 客户端</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>解析结果，失败返回 null</returns>
    public async Task<OnlineSkillResult?> ParseAsync(string url, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        var parser = _parsers.FirstOrDefault(p => p.CanParse(url));
        
        if (parser == null)
        {
            _logger.LogWarning("未找到支持该 URL 的解析器: {Url}", url);
            return null;
        }

        _logger.LogInformation("使用 {ParserName} 解析技能: {Url}", parser.Name, url);
        
        try
        {
            var result = await parser.ParseAsync(url, httpClient, cancellationToken);
            
            if (result == null)
            {
                _logger.LogWarning("解析器 {ParserName} 无法解析: {Url}", parser.Name, url);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析器 {ParserName} 解析失败: {Url}", parser.Name, url);
            return null;
        }
    }

    /// <summary>
    /// 下载技能 ZIP 包
    /// </summary>
    /// <param name="url">技能页面 URL</param>
    /// <param name="result">解析结果</param>
    /// <param name="httpClient">HTTP 客户端</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>ZIP 文件字节数组，失败返回 null</returns>
    public async Task<byte[]?> DownloadZipAsync(string url, OnlineSkillResult result, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        var parser = _parsers.FirstOrDefault(p => p.CanParse(url));
        
        if (parser == null)
        {
            _logger.LogWarning("未找到支持该 URL 的解析器: {Url}", url);
            return null;
        }

        _logger.LogInformation("使用 {ParserName} 下载技能: {DownloadUrl}", parser.Name, result.DownloadUrl);
        
        try
        {
            return await parser.DownloadZipAsync(result, httpClient, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载失败: {DownloadUrl}", result.DownloadUrl);
            return null;
        }
    }
}
