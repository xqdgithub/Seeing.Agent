using Microsoft.Extensions.Configuration;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 扩展加载状态
/// </summary>
public enum ExtensionLoadState
{
    /// <summary>首次加载</summary>
    First,

    /// <summary>已更新</summary>
    Updated,

    /// <summary>与上次相同</summary>
    Same
}

/// <summary>
/// 扩展来源
/// </summary>
public enum ExtensionSource
{
    /// <summary>Npm 包</summary>
    Npm,

    /// <summary>本地文件</summary>
    File
}

/// <summary>
/// 扩展元数据
/// </summary>
public class ExtensionMeta
{
    /// <summary>状态：首次加载 / 更新 / 相同</summary>
    public ExtensionLoadState State { get; set; } = ExtensionLoadState.First;

    /// <summary>扩展唯一标识</summary>
    public string Id { get; set; } = "";

    /// <summary>来源：npm 或 file</summary>
    public ExtensionSource Source { get; set; } = ExtensionSource.File;

    /// <summary>原始 spec</summary>
    public string Spec { get; set; } = "";

    /// <summary>目标路径（程序集路径）</summary>
    public string Target { get; set; } = "";

    /// <summary>版本（NuGet 插件）</summary>
    public string? Version { get; set; }

    /// <summary>加载次数</summary>
    public int LoadCount { get; set; } = 1;

    /// <summary>首次加载时间（Unix 毫秒）</summary>
    public long FirstTime { get; set; }

    /// <summary>最后加载时间（Unix 毫秒）</summary>
    public long LastTime { get; set; }

    /// <summary>指纹（用于检测变更）</summary>
    public string Fingerprint { get; set; } = "";
}