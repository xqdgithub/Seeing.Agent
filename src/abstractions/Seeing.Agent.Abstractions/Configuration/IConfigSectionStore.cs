namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 配置节读写窄接口 — 供能力包使用，避免直接依赖 UnifiedConfigManager。
/// </summary>
public interface IConfigSectionStore
{
    T GetSection<T>(string sectionName) where T : class, new();

    Task SaveSectionAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) where T : class;

    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
}
