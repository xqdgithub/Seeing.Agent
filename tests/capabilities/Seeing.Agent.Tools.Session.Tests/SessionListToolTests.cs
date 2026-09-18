using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Core.Tools.Session.Tests;

public class SessionListToolTests
{
    private static SessionListTool CreateTool(SessionToolTestHarness h) =>
        new(NullLogger<SessionListTool>.Instance, h.Sessions, h.Groups);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(string sessionId, IPermissionAuthorizerFactory? factory = null)
    {
        var ctx = new ToolContext { SessionId = sessionId };
        if (factory is not null)
            ctx.Services = new StubServiceProvider().Add(factory);
        return ctx;
    }

    [Fact]
    public async Task Related_Scope_Should_ListGroupMembers()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var child = await h.Groups.CreateChildAsync(
            root.Id, "build", "child-task", new List<SessionPermissionRule>(), null);
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { scope = "related" }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain(root.Id).And.Contain(child.Id);
        result.Output.Should().Contain("Child");
    }

    [Fact]
    public async Task All_Scope_Should_BeDenied_WhenAuthorizationRejects()
    {
        using var h = new SessionToolTestHarness();
        var root1 = await h.CreateRootGroupedAsync("r1");
        var root2 = await h.CreateRootGroupedAsync("r2");
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Deny);
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { scope = "all" }), Context(root1.Id, factory));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("权限");
        factory.LastRequest.Should().NotBeNull();
        factory.LastRequest!.SessionId.Should().Be(root1.Id);
        factory.LastRequest.Resource.Should().Be("session_list");
        result.Output.Should().NotContain(root2.Id);
    }

    [Fact]
    public async Task All_Scope_Should_ListPartitionSessions_WhenAuthorized()
    {
        using var h = new SessionToolTestHarness();
        var root1 = await h.CreateRootGroupedAsync("r1");
        var root2 = await h.CreateRootGroupedAsync("r2");
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { scope = "all" }), Context(root1.Id, factory));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain(root1.Id).And.Contain(root2.Id);
    }

    [Fact]
    public async Task Limit_Should_Truncate_And_ExposeNextOffset()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.Groups.CreateChildAsync(root.Id, "build", "c1", new List<SessionPermissionRule>(), null);
        await h.Groups.CreateChildAsync(root.Id, "build", "c2", new List<SessionPermissionRule>(), null);
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { scope = "related", limit = 1 }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("(显示 1/3，offset=1 续读)");
        result.Metadata["total"].Should().Be(3);
        result.Metadata["returned"].Should().Be(1);
        result.Metadata["truncated"].Should().Be(true);
        result.Metadata["next_offset"].Should().Be(1);
    }

    [Fact]
    public async Task InvalidScope_Should_Fail()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { scope = "bogus" }), Context(root.Id));

        result.Success.Should().BeFalse();
    }
}
