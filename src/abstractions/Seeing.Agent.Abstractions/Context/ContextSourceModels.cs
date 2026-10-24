namespace Seeing.Agent.Abstractions.Context;

/// <summary>
/// 上下文源的生效层级。
/// </summary>
public enum ContextSourceScope
{
    /// <summary>用户级：~/.seeing/</summary>
    User,

    /// <summary>项目级：{WorkspaceRoot}/</summary>
    Project,

    /// <summary>会话级：仅当前会话</summary>
    Session
}

/// <summary>
/// 初始渲染的落点。
/// </summary>
public enum ContextSourceTarget
{
    /// <summary>系统提示词末尾（变量替换之后追加，字节稳定）。</summary>
    System,

    /// <summary>会话消息（插入到指定消息之前，可随压缩/revert 重新可见）。</summary>
    Message
}

/// <summary>
/// 一次源读取的值状态。
/// </summary>
public enum ContextValueState
{
    /// <summary>尚无基线（首次参与装配）。</summary>
    Initial,

    /// <summary>读取成功，值可用。</summary>
    Value,

    /// <summary>暂时不可读（被占用 / 无权限等）；不参与 diff，保留上一次已叙述内容。</summary>
    Unavailable,

    /// <summary>读取成功且确认已不存在；参与 diff，叙述删除。</summary>
    Removed
}

/// <summary>
/// 上下文源的稳定标识（约定取实现类型全名），同时作为状态 JSON 中 sources 的键。
/// </summary>
/// <param name="Value">标识文本；须稳定，不得随运行时数据变化。</param>
public readonly record struct ContextSourceKey(string Value);

/// <summary>
/// 源读取结果。
/// </summary>
/// <param name="State">值状态；<see cref="ContextValueState.Unavailable"/> 不参与 diff。</param>
/// <param name="Canonical">用于计算规范化哈希与 diff 的值（不含 Payload），落盘。</param>
/// <param name="Payload">仅本次渲染可用的原始数据（如正文），每次读取重新取得，不落盘。</param>
public sealed record ContextSourceValue(
    ContextValueState State,
    JsonElement? Canonical,
    object? Payload);
