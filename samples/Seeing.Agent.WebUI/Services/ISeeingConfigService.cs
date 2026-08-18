namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI 配置服务契约 - 封装 UnifiedConfigManager 提供页面友好的 API
/// </summary>
public interface ISeeingConfigService
{
    /// <summary>重新加载全部配置</summary>
    Task ReloadAsync(CancellationToken ct = default);
}
