namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 配置变更事件参数 - 支持细粒度变更通知
/// </summary>
public sealed class ConfigChangedEventArgs : EventArgs
{
    /// <summary>
    /// 发生变更的配置节名称列表
    /// <para>空数组表示全量重载（如初始化或外部文件变更）</para>
    /// </summary>
    public string[] ChangedSections { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 变更节内的细粒度键（如 Providers 节下变更的 provider id）。
    /// <para>空表示未提供作用域，消费方应回退为按节全量处理。</para>
    /// </summary>
    public IReadOnlyList<string> ChangedKeys { get; init; } = Array.Empty<string>();

    /// <summary>变更时间</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// 是否涉及指定配置节
    /// <para>空数组（全量重载）时返回 true</para>
    /// </summary>
    public bool ContainsSection(string sectionName)
        => ChangedSections.Length == 0 || ChangedSections.Contains(sectionName);

    /// <summary>
    /// 是否涉及任一指定配置节
    /// </summary>
    public bool ContainsAnySection(params string[] sectionNames)
        => ChangedSections.Length == 0 || sectionNames.Any(ChangedSections.Contains);
}
