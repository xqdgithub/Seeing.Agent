using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Permission;
using Xunit;

namespace Seeing.Gateway.Tests.Permission;

public class GatewayPermissionChannelTests
{
    private static GatewayPermissionChannel CreateChannel(string permissionMode) =>
        new(Options.Create(new GatewayOptions { PermissionMode = permissionMode }));

    [Fact]
    public void TryAutoApprove_AutoApproveMode_ShouldReturnAllow()
    {
        var channel = CreateChannel("auto_approve");

        var effect = channel.TryAutoApprove(CreateRequest());

        effect.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void TryAutoApprove_InteractiveMode_ShouldReturnNull()
    {
        var channel = CreateChannel("interactive");

        var effect = channel.TryAutoApprove(CreateRequest());

        effect.Should().BeNull();
    }

    [Fact]
    public void TryAutoApprove_ShouldReflectCurrentMode_ForRequestsEvaluatedAfterChange()
    {
        var request = CreateRequest();
        var options = new MutableOptions<GatewayOptions>(new GatewayOptions { PermissionMode = "interactive" });
        var channel = new GatewayPermissionChannel(options);

        channel.TryAutoApprove(request).Should().BeNull();

        options.Value = new GatewayOptions { PermissionMode = "auto_approve" };

        channel.TryAutoApprove(request).Should().Be(PermissionEffect.Allow);

        // 已知限制（spec §7.2/§8）：PermissionMode 变更仅对之后进入 BeginAsync 的**新请求**生效；
        // 已在途（已登记）请求不会因模式变更被重评估——判定在请求进入时点一次完成。
    }

    private sealed class MutableOptions<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; set; } = value;
    }

    private static PermissionRequest CreateRequest() => new()
    {
        RequestId = "req_1",
        SessionId = "ses_1",
        PermissionKind = "tool.execute"
    };
}
