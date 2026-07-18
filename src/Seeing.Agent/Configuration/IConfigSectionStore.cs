namespace Seeing.Agent.Configuration;

/// <summary>
/// 配置节读写窄接口 — 供 Memory/ACP 等可选模块使用，避免直接依赖 <see cref="UnifiedConfigManager"/>。
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
