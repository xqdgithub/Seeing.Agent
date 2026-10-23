using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>投递结果 → 面向模型/用户的中文文案。</summary>
internal static class ChannelDeliveryMessages
{
    /// <summary>成功文案（「已提交发送」表示网关帧已接收，非平台送达）。</summary>
    public const string Delivered = "已提交发送";

    public static string For(OutboundDeliveryResult result)
    {
        if (result.Delivered)
            return Delivered;

        return result.FailureKind switch
        {
            OutboundFailureKind.NotInDirectory => DetailOr(result, "该用户尚未与机器人建立对话，无法推送"),
            OutboundFailureKind.OptedOut => "用户已退订",
            OutboundFailureKind.RateLimited => "超出投递频率限制",
            OutboundFailureKind.ChannelUnavailable => DetailOr(result, "通道投递未配置或通道不可用"),
            OutboundFailureKind.AttachmentUnsupported => WithDetail("目标对话不支持该附件", result.Detail),
            OutboundFailureKind.AttachmentTooLarge => WithDetail("附件超过通道大小上限", result.Detail),
            OutboundFailureKind.AttachmentUnavailable => WithDetail("附件不可用", result.Detail),
            OutboundFailureKind.PlatformError => WithDetail("平台错误", result.Detail),
            _ => string.IsNullOrWhiteSpace(result.Detail) ? "投递失败" : result.Detail!
        };
    }

    /// <summary>结构化 Detail 优先（NotInDirectory / ChannelUnavailable），缺失时回落固定文案。</summary>
    private static string DetailOr(OutboundDeliveryResult result, string fallback) =>
        string.IsNullOrWhiteSpace(result.Detail) ? fallback : result.Detail!;

    private static string WithDetail(string prefix, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}：{detail}";
}
