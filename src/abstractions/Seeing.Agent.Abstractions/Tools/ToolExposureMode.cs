namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具暴露模式：描述单个工具在本轮 LLM 请求中的呈现方式。
/// </summary>
public enum ToolExposureMode
{
    /// <summary>内联：完整 schema 直接进入 tools 数组。</summary>
    Inline,

    /// <summary>延迟：仅以目录条目暴露，被发现/声明后才转为内联。</summary>
    Deferred,

    /// <summary>隐藏：本轮完全不暴露。</summary>
    Hidden
}
