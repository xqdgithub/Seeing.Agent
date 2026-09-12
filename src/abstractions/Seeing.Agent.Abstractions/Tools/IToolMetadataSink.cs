namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具元数据出口。
/// </summary>
/// <remarks>
/// <para>
/// <b>当前生产路径未接线</b>（<c>ToolManager</c> 传入的 <c>setMetadata</c> 为 null）：
/// 调用 <see cref="SetMetadata"/> 不会产生任何 UI/事件效果。
/// </para>
/// <para>
/// 工具执行进度与流式输出请使用 <see cref="IToolEventSink"/> /
/// <c>ToolCallEvent</c>（Running + Output）。终态结构化字段写入
/// <c>ToolResult.Metadata</c>，由 AgentExecutor 打进 Complete 事件。
/// </para>
/// </remarks>
public interface IToolMetadataSink
{
    /// <summary>设置元数据（生产路径当前为 no-op，请勿依赖）</summary>
    void SetMetadata(string key, Dictionary<string, object>? value);
}
