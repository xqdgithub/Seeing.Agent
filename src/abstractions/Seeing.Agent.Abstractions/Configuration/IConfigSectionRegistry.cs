namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 配置节注册表：扩展包声明自身提供的配置节元信息。
/// </summary>
public interface IConfigSectionRegistry
{
    /// <summary>注册配置节元信息。</summary>
    void Register(ConfigSectionMeta meta);

    /// <summary>已注册的全部配置节。</summary>
    IReadOnlyCollection<ConfigSectionMeta> Sections { get; }

    /// <summary>按键查找配置节元信息。</summary>
    bool TryGet(string key, out ConfigSectionMeta meta);
}
