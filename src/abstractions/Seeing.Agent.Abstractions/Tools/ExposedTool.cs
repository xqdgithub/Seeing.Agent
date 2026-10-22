using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 本轮一个可用工具的完整视图：定义、展示签名与暴露模式。
/// <para>
/// 这是"本轮工具列表"的唯一形态，inline 请求、目录文本、检索语料、可观测计数都从它派生，
/// 不再各自向工具注册表查询。
/// </para>
/// </summary>
/// <param name="Id">工具 ID。</param>
/// <param name="Namespace">命名空间（MCP 工具为 <c>mcp__{server}</c>，内置工具为空串）。</param>
/// <param name="Signature">单行展示签名（含截断描述）。</param>
/// <param name="Schema">完整工具定义。</param>
/// <param name="EstimatedTokens">该工具定义的估算 token。</param>
/// <param name="Mode">本轮暴露模式。</param>
public sealed record ExposedTool(
    string Id,
    string Namespace,
    string Signature,
    FunctionToolSchema Schema,
    int EstimatedTokens,
    ToolExposureMode Mode);

/// <summary>
/// 本轮实际生效的暴露布局（结果态，不含配置层的 <c>Auto</c>）。
/// </summary>
public enum ToolExposureLayout
{
    /// <summary>全部可用工具内联进本轮请求。</summary>
    Flat,

    /// <summary>仅 pinned、已发现与检索元工具内联，其余进目录。</summary>
    Tiered
}
