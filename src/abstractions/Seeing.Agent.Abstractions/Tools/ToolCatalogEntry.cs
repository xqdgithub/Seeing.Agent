namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具目录条目：延迟暴露工具在目录中的纯数据描述（不含校验逻辑）。
/// </summary>
/// <param name="Id">工具 ID。</param>
/// <param name="Namespace">工具命名空间（可空）。</param>
/// <param name="Signature">工具签名（调用形态摘要）。</param>
/// <param name="Summary">已按上限截断后的摘要。</param>
/// <param name="EstimatedTokens">该条目估算 token 数。</param>
/// <param name="Pinned">是否固定（不参与预算裁剪）。</param>
/// <param name="Mode">本条目的暴露模式。</param>
public sealed record ToolCatalogEntry(
    string Id,
    string? Namespace,
    string Signature,
    string Summary,
    int EstimatedTokens,
    bool Pinned,
    ToolExposureMode Mode);
