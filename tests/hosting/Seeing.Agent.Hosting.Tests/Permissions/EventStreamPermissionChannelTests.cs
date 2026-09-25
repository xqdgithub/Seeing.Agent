using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Hosting.Web.Permissions;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Permissions;

/// <summary>
/// Web 事件流权限通道测试 — 对齐 Gateway 侧 GatewayPermissionChannelTests 形态。
/// <para>
/// 通道本身无挂起状态：不参与自动放行（TryAutoApprove 恒 null，决策由授权引擎负责），
/// Present/Dismiss 仅作为无副作用收敛点（呈现与广播由权限管理器经执行事件流完成）。
/// </para>
/// </summary>
public class EventStreamPermissionChannelTests
{
    private static EventStreamPermissionChannel CreateChannel() => new();

    [Fact]
    public void TryAutoApprove_ShouldAlwaysReturnNull_DelegatesToAuthorizationEngine()
    {
        var channel = CreateChannel();

        channel.TryAutoApprove(CreateRequest()).Should().BeNull(
            "Web 通道不承载自动放行策略，交由统一授权引擎判定");
    }

    [Fact]
    public async Task PresentAsync_ShouldComplete_WithoutHoldingState()
    {
        var channel = CreateChannel();

        await channel.PresentAsync(CreateRequest(), CancellationToken.None);

        // 无状态通道：重复呈现同一请求也不应抛错或产生副作用
        await channel.PresentAsync(CreateRequest(), CancellationToken.None);
    }

    [Fact]
    public async Task DismissAsync_ShouldComplete_WithoutHoldingState()
    {
        var channel = CreateChannel();

        await channel.DismissAsync(CreateResolution(PermissionEffect.Allow), CancellationToken.None);
        await channel.DismissAsync(CreateResolution(PermissionEffect.Deny), CancellationToken.None);
    }

    [Fact]
    public async Task PresentAsync_WhenCancelled_ShouldNotThrow_BecauseChannelIsInert()
    {
        var channel = CreateChannel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 通道为纯收敛点，不观察取消令牌（呈现由管理器经事件流广播）
        await channel.PresentAsync(CreateRequest(), cts.Token);
    }

    private static PermissionRequest CreateRequest() => new()
    {
        RequestId = "req_1",
        SessionId = "ses_1",
        PermissionKind = "tool.execute",
        Resource = "bash"
    };

    private static PermissionResolution CreateResolution(PermissionEffect effect) => new()
    {
        RequestId = "req_1",
        SessionId = "ses_1",
        Decision = effect
    };
}
