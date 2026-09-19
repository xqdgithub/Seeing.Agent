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
    public void Resolve_SubAgentFollowGlobal_ParentEnabled_ShouldReturnAllow()
    {
        var policy = ChildPolicy(parent: SessionAutoApprove.Enabled);

        var result = policy.Resolve(Request(sessionId: "child"));

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_SubAgentFollowGlobal_ParentDisabled_ShouldForceInteraction()
    {
        var policy = ChildPolicy(parent: SessionAutoApprove.Disabled);

        var result = policy.Resolve(Request(sessionId: "child"));

        result.Should().Be(PermissionEffect.Ask);
    }

    [Fact]
    public void Resolve_SubAgentFollowGlobal_ParentFollowGlobal_GlobalTrue_ShouldReturnAllow()
    {
        var policy = ChildPolicy(parent: SessionAutoApprove.FollowGlobal, globalAutoApproveAll: true);

        var result = policy.Resolve(Request(sessionId: "child"));

        result.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_SubAgentOwnDisabled_ShouldBeatParentEnabled()
    {
        var policy = CreatePolicy(
            sessions: new Dictionary<string, SessionData>
            {
                ["child"] = Session("child", SessionAutoApprove.Disabled, SessionKind.SubAgent),
                ["parent"] = Session("parent", SessionAutoApprove.Enabled)
            },
            parents: new Dictionary<string, string> { ["child"] = "parent" });

        policy.Resolve(Request(sessionId: "child")).Should().Be(PermissionEffect.Ask);
    }

    [Fact]
    public void Resolve_NestedSubAgent_FollowsRootParent_ShouldReturnAllow()
    {
        var policy = CreatePolicy(
            sessions: new Dictionary<string, SessionData>
            {
                ["grand"] = Session("grand", SessionAutoApprove.FollowGlobal, SessionKind.SubAgent),
                ["child"] = Session("child", SessionAutoApprove.FollowGlobal, SessionKind.SubAgent),
                ["parent"] = Session("parent", SessionAutoApprove.Enabled)
            },
            parents: new Dictionary<string, string>
            {
                ["grand"] = "child",
                ["child"] = "parent"
            });

        policy.Resolve(Request(sessionId: "grand")).Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_RootSession_ShouldNotFollowParent()
    {
        var policy = CreatePolicy(
            sessions: new Dictionary<string, SessionData>
            {
                ["forked"] = Session("forked", SessionAutoApprove.FollowGlobal, SessionKind.Root),
                ["source"] = Session("source", SessionAutoApprove.Enabled)
            },
            parents: new Dictionary<string, string> { ["forked"] = "source" });

        policy.Resolve(Request(sessionId: "forked")).Should().BeNull();
    }

    [Fact]
    public void Resolve_SubAgent_NoParentIndexed_ShouldFallBackToGlobal()
    {
        var policy = CreatePolicy(
            globalAutoApproveAll: true,
            sessions: new Dictionary<string, SessionData>
            {
                ["child"] = Session("child", SessionAutoApprove.FollowGlobal, SessionKind.SubAgent)
            });

        policy.Resolve(Request(sessionId: "child")).Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void Resolve_RequireInteraction_ShouldReturnNullEvenWithGlobalTrue()
    {
        var policy = CreatePolicy(globalAutoApproveAll: true, session: Session("s1", SessionAutoApprove.Enabled));

        var result = policy.Resolve(Request(requireInteraction: true, @override: SessionAutoApprove.Enabled));

        result.Should().BeNull();
    }

    private static PermissionRequest Request(
        string sessionId = "s1",
        bool requireInteraction = false,
        SessionAutoApprove? @override = null) => new()
        {
            SessionId = sessionId,
            PermissionKind = "tool.execute",
            RequireInteraction = requireInteraction,
            Override = @override
        };

    private static SessionData Session(
        string id,
        SessionAutoApprove value,
        SessionKind kind = SessionKind.Root) => new()
        {
            Id = id,
            Kind = kind,
            AutoApprove = value
        };

    /// <summary>子会话（自身 FollowGlobal）绑定到 parent；用于验证实时父链上溯。</summary>
    private static EffectivePermissionPolicy ChildPolicy(
        SessionAutoApprove parent,
        bool globalAutoApproveAll = false) =>
        CreatePolicy(
            globalAutoApproveAll: globalAutoApproveAll,
            sessions: new Dictionary<string, SessionData>
            {
                ["child"] = Session("child", SessionAutoApprove.FollowGlobal, SessionKind.SubAgent),
                ["parent"] = Session("parent", parent)
            },
            parents: new Dictionary<string, string> { ["child"] = "parent" });

    private static EffectivePermissionPolicy CreatePolicy(
        bool globalAutoApproveAll = false,
        SessionData? session = null,
        IReadOnlyDictionary<string, SessionData>? sessions = null,
        IReadOnlyDictionary<string, string>? parents = null)
    {
        var sessionsMock = new Mock<ISessionManager>();
        sessionsMock.Setup(s => s.Get(It.IsAny<string>()))
            .Returns((string id) => sessions is null
                ? session
                : sessions.TryGetValue(id, out var found) ? found : null);

        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new SeeingAgentOptions
        {
            Permission = new PermissionOptions { AutoApproveAll = globalAutoApproveAll }
        });

        var groups = new Mock<ISessionGroupManager>();
        groups.Setup(g => g.TryGetParent(It.IsAny<string>(), out It.Ref<string?>.IsAny))
            .Returns((string id, out string? parent) =>
            {
                parent = parents is not null && parents.TryGetValue(id, out var p) ? p : null;
                return !string.IsNullOrEmpty(parent);
            });

        return new EffectivePermissionPolicy(sessionsMock.Object, options.Object, groups.Object);
    }
}
