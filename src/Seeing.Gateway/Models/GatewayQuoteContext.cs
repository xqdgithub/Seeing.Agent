namespace Seeing.Gateway.Models;

/// <summary>
/// 渠道侧引用消息上下文（与用户当前 <see cref="GatewayRequest.Input"/> 并列）。
/// </summary>
public record GatewayQuoteContext
{
    /// <summary>引用类型：text / image / mixed / voice / file / video</summary>
    public string? MsgType { get; init; }

    /// <summary>引用正文，复用 <see cref="GatewayContentPart"/> 多模态 union</summary>
    public IReadOnlyList<GatewayContentPart>? Content { get; init; }

    /// <summary>来源渠道标识，如 wecom（可选，便于调试/扩展）</summary>
    public string? SourceChannel { get; init; }
}
