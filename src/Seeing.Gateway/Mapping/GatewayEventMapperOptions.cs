namespace Seeing.Gateway.Mapping;

/// <summary>
/// <see cref="GatewayEventMapper"/> 映射选项
/// </summary>
public record GatewayEventMapperOptions
{
    /// <summary>是否过滤 reasoning/thinking 增量（默认过滤）</summary>
    public bool FilterThinking { get; init; } = true;
}
