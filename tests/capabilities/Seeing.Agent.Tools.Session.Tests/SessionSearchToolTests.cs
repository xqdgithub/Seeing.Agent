using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Core.Tools.Session.Tests;

public class SessionSearchToolTests
{
    private static SessionSearchTool CreateTool(SessionToolTestHarness h) =>
        new(NullLogger<SessionSearchTool>.Instance, h.Sessions, h.Groups);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(string sessionId, IPermissionAuthorizerFactory? factory = null)
    {
        var ctx = new ToolContext { SessionId = sessionId };
        if (factory is not null)
            ctx.Services = new StubServiceProvider().Add(factory);
        return ctx;
    }

    [Fact]
    public void Capabilities_Should_SkipOutputLimiter()
    {
        using var h = new SessionToolTestHarness();
        var tool = CreateTool(h);
        tool.Capabilities.Should().NotBeNull();
        tool.Capabilities!["output.skip"].Should().Be("true");
    }

    [Fact]
    public async Task Search_Should_MatchContent_ByPattern()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var first = await h.AddMessageAsync(root.Id, "assistant", "hello world");
        var second = await h.AddMessageAsync(root.Id, "user", "goodbye world");
        await h.AddMessageAsync(root.Id, "assistant", "no match here");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { pattern = "world" }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain($"{first.Id}:assistant:step0:hello world");
        result.Output.Should().Contain($"{second.Id}:user:step0:goodbye world");
        result.Metadata["matches"].Should().Be(2);
        result.Metadata["returned"].Should().Be(2);
    }

    [Fact]
    public async Task Search_Should_Filter_ByRole()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "assistant", "alpha");
        var user = await h.AddMessageAsync(root.Id, "user", "alpha");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { pattern = "alpha", role = "user" }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain(user.Id);
        result.Output.Should().NotContain(":assistant:");
        result.Metadata["matches"].Should().Be(1);
    }

    [Fact]
    public async Task Search_Should_IncludeContext_WithPrefix()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var before = await h.AddMessageAsync(root.Id, "assistant", "alpha");
        var hit = await h.AddMessageAsync(root.Id, "user", "beta target");
        var after = await h.AddMessageAsync(root.Id, "assistant", "gamma");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { pattern = "target", context = 1 }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain($">{before.Id}:");
        result.Output.Should().Contain($">{after.Id}:");
        result.Output.Should().Contain($"{hit.Id}:user:step0:beta target");
    }

    [Fact]
    public async Task Search_Should_Paginate_WithMetadata()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "match one");
        await h.AddMessageAsync(root.Id, "user", "match two");
        await h.AddMessageAsync(root.Id, "user", "match three");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { pattern = "match", limit = 1 }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Metadata["matches"].Should().Be(3);
        result.Metadata["returned"].Should().Be(1);
        result.Metadata["truncated"].Should().Be(true);
        result.Metadata["next_offset"].Should().Be(1);
    }

    [Fact]
    public async Task Search_Should_NotLeakReasoningContent()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "assistant", "visible", reasoning: "SECRET_REASONING");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { pattern = "SECRET" }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().NotContain("SECRET_REASONING");
        result.Metadata["matches"].Should().Be(0);
    }

    [Fact]
    public async Task Search_SameGroup_Should_NotRequireAuthorization()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var child = await h.Groups.CreateChildAsync(
            root.Id, "build", "child", new List<SessionPermissionRule>(), null);
        await h.AddMessageAsync(child.Id, "user", "child content");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = child.Id, pattern = "child" }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("child content");
    }

    [Fact]
    public async Task Search_CrossGroup_Should_RequireAuthorization()
    {
        using var h = new SessionToolTestHarness();
        var root1 = await h.CreateRootGroupedAsync("r1");
        var root2 = await h.CreateRootGroupedAsync("r2");
        await h.AddMessageAsync(root2.Id, "user", "private content");
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Deny);
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root2.Id, pattern = "private" }), Context(root1.Id, factory));

        result.Success.Should().BeFalse();
        result.Output.Should().NotContain("private content");
        factory.LastRequest.Should().NotBeNull();
        factory.LastRequest!.Resource.Should().Be("session_search");
    }

    [Fact]
    public async Task Search_CrossGroup_Should_Succeed_WhenAuthorized()
    {
        using var h = new SessionToolTestHarness();
        var root1 = await h.CreateRootGroupedAsync("r1");
        var root2 = await h.CreateRootGroupedAsync("r2");
        await h.AddMessageAsync(root2.Id, "user", "shared content");
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root2.Id, pattern = "shared" }), Context(root1.Id, factory));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("shared content");
    }

    [Fact]
    public async Task Search_Should_Fail_OnInvalidRegex()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { pattern = "(" }), Context(root.Id));

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Search_Should_Fail_WhenPatternMissing()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { }), Context(root.Id));

        result.Success.Should().BeFalse();
    }
}
