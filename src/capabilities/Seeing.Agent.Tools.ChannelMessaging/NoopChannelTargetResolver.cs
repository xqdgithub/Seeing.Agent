using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 未组合通联模块时的缺省目标解析器：不解析会话目标，明确报告通道未配置。
/// 由 <see cref="ChannelMessagingModule"/> 注册，<c>AddSeeingComms</c> 后注册覆盖。
/// </summary>
public sealed class NoopChannelTargetResolver : IChannelTargetResolver
{
    /// <summary>与 <see cref="NoopChannelMessageSink.UnavailableDetail"/> 保持一致的失败说明。</summary>
    public const string UnavailableDetail = NoopChannelMessageSink.UnavailableDetail;

    /// <inheritdoc />
    public Task<ChannelTargetResolution> ResolveSessionTargetAsync(
        string sessionId, CancellationToken ct = default)
        => Task.FromResult(
            ChannelTargetResolution.Fail(OutboundFailureKind.ChannelUnavailable, UnavailableDetail));
}
