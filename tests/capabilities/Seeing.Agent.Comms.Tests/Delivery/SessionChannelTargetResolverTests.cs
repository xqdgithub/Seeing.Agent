using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.Delivery;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Comms.Tests.Delivery;

/// <summary>会话 → 出站目标：自身路由优先，未绑定则沿父链回溯（≤8 层），只读不建会话。</summary>
public class SessionChannelTargetResolverTests
{
    [Fact]
    public async Task ResolveSessionTargetAsync_ShouldUseOwnRoute_AndFillRecipientForDirect()
    {
        var resolver = NewResolver(
            new()
            {
                ["s1"] = new SessionChannelRoute
                {
                    ChannelId = "qq",
                    Kind = "direct",
                    ExternalId = "u1",
                    Title = "Ada",
                },
            },
            new());

        var result = await resolver.ResolveSessionTargetAsync("s1");

        result.FailureKind.Should().BeNull();
        result.Target.Should().NotBeNull();
        result.Target!.Conversation.ChannelId.Should().Be("qq");
        result.Target.Conversation.Kind.Should().Be(ConversationKind.Direct);
        result.Target.Conversation.ExternalId.Should().Be("u1");
        result.Target.Conversation.Title.Should().Be("Ada");
        result.Target.RecipientUserId.Should().Be("u1");
    }

    [Fact]
    public async Task ResolveSessionTargetAsync_ShouldWalkParentChain()
    {
        // 子会话无路由；父链第 2 层绑定群聊
        var resolver = NewResolver(
            new()
            {
                ["root"] = new SessionChannelRoute
                {
                    ChannelId = "qq",
                    Kind = "group",
                    ExternalId = "g1",
                },
            },
            new()
            {
                ["child"] = "mid",
                ["mid"] = "root",
            });

        var result = await resolver.ResolveSessionTargetAsync("child");

        result.Target.Should().NotBeNull();
        result.Target!.Conversation.Kind.Should().Be(ConversationKind.Group);
        result.Target.Conversation.ExternalId.Should().Be("g1");
        // 群聊不补收件人：群内定向由调用方显式指定
        result.Target.RecipientUserId.Should().BeNull();
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public async Task ResolveSessionTargetAsync_ShouldWalkAtMostEightParents(int depth, bool expectFound)
    {
        var routes = new Dictionary<string, SessionChannelRoute>();
        var parents = new Dictionary<string, string?>();
        for (var i = 0; i < depth; i++)
            parents[$"s{i}"] = $"s{i + 1}";

        routes[$"s{depth}"] = new SessionChannelRoute
        {
            ChannelId = "qq",
            Kind = "direct",
            ExternalId = "u1",
        };

        var result = await NewResolver(routes, parents).ResolveSessionTargetAsync("s0");

        if (expectFound)
        {
            result.Target.Should().NotBeNull();
            result.FailureKind.Should().BeNull();
        }
        else
        {
            result.Target.Should().BeNull();
            result.FailureKind.Should().Be(OutboundFailureKind.NotInDirectory);
        }
    }

    [Fact]
    public async Task ResolveSessionTargetAsync_ShouldReturnNotInDirectory_WhenNoRouteInChain()
    {
        var resolver = NewResolver(
            new(),
            new()
            {
                ["s1"] = "s2",
                ["s2"] = null,
            });

        var result = await resolver.ResolveSessionTargetAsync("s1");

        result.Target.Should().BeNull();
        result.FailureKind.Should().Be(OutboundFailureKind.NotInDirectory);
        result.Detail.Should().Be("当前会话及父会话均未绑定通道");
    }

    [Fact]
    public async Task ResolveSessionTargetAsync_ShouldReturnNotInDirectory_ForEmptySessionId()
    {
        var groups = new Mock<ISessionGroupManager>();
        var resolver = new SessionChannelTargetResolver(Mock.Of<ISessionManager>(), groups.Object);

        var result = await resolver.ResolveSessionTargetAsync(" ");

        result.FailureKind.Should().Be(OutboundFailureKind.NotInDirectory);
        groups.Verify(g => g.GetParentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveSessionTargetAsync_ShouldReturnNotInDirectory_WhenSessionDoesNotExist()
    {
        var groups = new Mock<ISessionGroupManager>();
        var resolver = new SessionChannelTargetResolver(Mock.Of<ISessionManager>(), groups.Object);

        var result = await resolver.ResolveSessionTargetAsync("missing");

        result.FailureKind.Should().Be(OutboundFailureKind.NotInDirectory);
        // 会话不存在时不猜测关系、不建会话
        groups.Verify(g => g.GetParentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveSessionTargetAsync_ShouldPreferInMemorySession_WithoutReloading()
    {
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.TryGetOrLoadAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionData
            {
                Id = "s1",
                ChannelRoute = new SessionChannelRoute
                {
                    ChannelId = "qq",
                    Kind = "group",
                    ExternalId = "g1",
                },
            });
        var groups = new Mock<ISessionGroupManager>();
        var resolver = new SessionChannelTargetResolver(sessions.Object, groups.Object);

        var result = await resolver.ResolveSessionTargetAsync("s1");

        result.Target.Should().NotBeNull();
        result.Target!.Conversation.ExternalId.Should().Be("g1");
        // 只走「内存优先」入口，绝不触发会覆盖缓存的 LoadAsync
        sessions.Verify(s => s.TryGetOrLoadAsync("s1", It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.LoadAsync(It.IsAny<string>()), Times.Never);
    }

    private static SessionChannelTargetResolver NewResolver(
        Dictionary<string, SessionChannelRoute> routes,
        Dictionary<string, string?> parents)
    {
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.TryGetOrLoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                routes.TryGetValue(id, out var route)
                    ? new SessionData { Id = id, ChannelRoute = route }
                    : parents.ContainsKey(id) ? new SessionData { Id = id } : null);

        var groups = new Mock<ISessionGroupManager>();
        groups.Setup(g => g.GetParentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                parents.TryGetValue(id, out var parent) ? parent : null);

        return new SessionChannelTargetResolver(sessions.Object, groups.Object);
    }
}
