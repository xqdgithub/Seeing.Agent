using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 生效开关解析（spec §5.3）：Override &gt; 会话三态 &gt; 全局；RequireInteraction 恒 null；
/// 返回值只允许 Allow / Ask / null（不返回 Deny）。
/// </summary>
public class EffectivePermissionPolicyTests
{
    [Fact]
    public void Resolve_NoOverrideSessionMissingGlobalFalse_ShouldReturnNull()
    {
        var policy = CreatePolicy();

        var result = policy.Resolve(Request());

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_SessionMissing_GlobalTrue_ShouldReturnAllow()
    {
        var policy = CreatePolicy(globalAutoApproveAll: true);

        var result = policy.Resolve(Request());

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_GlobalAutoApproveAll_ShouldReturnAllow()
    {
        var policy = CreatePolicy(globalAutoApproveAll: true);

        var result = policy.Resolve(Request());

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_SessionEnabled_ShouldReturnAllow()
    {
        var policy = CreatePolicy(session: Session("s1", SessionAutoApprove.Enabled));

        var result = policy.Resolve(Request());

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_SessionDisabled_ShouldForceInteraction()
    {
        var policy = CreatePolicy(globalAutoApproveAll: true, session: Session("s1", SessionAutoApprove.Disabled));

        var result = policy.Resolve(Request());

        result.Should().Be(PermissionEffect.Ask);
        result.Should().NotBe(PermissionEffect.Deny);
    }

    [Fact]
    public void Resolve_OverrideEnabled_ShouldBeatSessionDisabled()
    {
        var policy = CreatePolicy(session: Session("s1", SessionAutoApprove.Disabled));

        var result = policy.Resolve(Request(@override: SessionAutoApprove.Enabled));

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_OverrideDisabled_ShouldBeatSessionEnabled()
    {
        var policy = CreatePolicy(session: Session("s1", SessionAutoApprove.Enabled));

        var result = policy.Resolve(Request(@override: SessionAutoApprove.Disabled));

        result.Should().Be(PermissionEffect.Ask);
    }

    [Fact]
    public void Resolve_OverrideFollowGlobal_ShouldFallBackToSession()
    {
        var policy = CreatePolicy(session: Session("s1", SessionAutoApprove.Enabled));

        var result = policy.Resolve(Request(@override: SessionAutoApprove.FollowGlobal));

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_SessionFollowGlobal_ShouldFallBackToGlobal()
    {
        var policy = CreatePolicy(globalAutoApproveAll: true, session: Session("s1", SessionAutoApprove.FollowGlobal));

        var result = policy.Resolve(Request());

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_RequireInteraction_ShouldReturnNullEvenWithGlobalTrue()
    {
        var policy = CreatePolicy(globalAutoApproveAll: true, session: Session("s1", SessionAutoApprove.Enabled));

        var result = policy.Resolve(Request(requireInteraction: true, @override: SessionAutoApprove.Enabled));

        result.Should().BeNull();
    }

    private static PermissionRequest Request(
        bool requireInteraction = false,
        SessionAutoApprove? @override = null) => new()
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            RequireInteraction = requireInteraction,
            Override = @override
        };

    private static SessionData Session(string id, SessionAutoApprove value) => new()
    {
        Id = id,
        AutoApprove = value
    };

    private static EffectivePermissionPolicy CreatePolicy(
        bool globalAutoApproveAll = false,
        SessionData? session = null)
    {
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get(It.IsAny<string>())).Returns(session);

        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new SeeingAgentOptions
        {
            Permission = new PermissionOptions { AutoApproveAll = globalAutoApproveAll }
        });

        return new EffectivePermissionPolicy(sessions.Object, options.Object);
    }
}
