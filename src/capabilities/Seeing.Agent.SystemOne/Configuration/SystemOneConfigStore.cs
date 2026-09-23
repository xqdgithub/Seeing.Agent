using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>
/// SystemOne 配置节存取：基于注入的 <see cref="IConfigSectionStore"/> 读取
/// <c>systemone.json</c>（顶层 <c>Dictionary&lt;string, SystemOneProviderConfig&gt;</c>）。
/// </summary>
public sealed class SystemOneConfigStore
{
    /// <summary>配置节键名。</summary>
    public const string SectionName = "SystemOne";

    /// <summary>配置文件名称（用户级顶层文件）。</summary>
    public const string FileName = "systemone.json";

    private readonly IConfigSectionStore _store;

    /// <summary>构造配置节存取器。</summary>
    public SystemOneConfigStore(IConfigSectionStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>配置节元信息（Key/文件名/UserOnly/值类型）。</summary>
    public static ConfigSectionMeta SectionMeta { get; } = new(
        SectionName,
        FileName,
        ConfigScope.UserOnly,
        typeof(Dictionary<string, SystemOneProviderConfig>));

    /// <summary>读取 provider 配置字典；缺失时返回空字典（不返回 null）。</summary>
    public Dictionary<string, SystemOneProviderConfig> Read()
        => _store.GetSection<Dictionary<string, SystemOneProviderConfig>>(SectionName)
           ?? new Dictionary<string, SystemOneProviderConfig>(StringComparer.OrdinalIgnoreCase);
}
