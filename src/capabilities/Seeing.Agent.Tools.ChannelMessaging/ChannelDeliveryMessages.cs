using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Tools.ChannelMessaging;

internal static class ChannelDeliveryMessages
{
    public static string For(OutboundDeliveryResult result)
    {
        if (result.Delivered)
            return "已投递";

        return result.FailureKind switch
        {
            OutboundFailureKind.NotInDirectory => "该用户尚未与机器人建立对话，无法推送",
            OutboundFailureKind.OptedOut => "用户已退订",
            OutboundFailureKind.RateLimited => "超出投递频率限制",
            OutboundFailureKind.ChannelUnavailable => "通道投递未配置或通道不可用",
            OutboundFailureKind.PlatformError => string.IsNullOrWhiteSpace(result.Detail)
                ? "平台错误"
                : $"平台错误：{result.Detail}",
            _ => string.IsNullOrWhiteSpace(result.Detail) ? "投递失败" : result.Detail
        };
    }
}
