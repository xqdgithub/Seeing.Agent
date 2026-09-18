using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Core.Tools.Session.Tests;

public class SessionReadToolTests
{
    private static SessionReadTool CreateTool(SessionToolTestHarness h) =>
        new(NullLogger<SessionReadTool>.Instance, h.Sessions, h.Groups);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(string sessionId, IPermissionAuthorizerFactory? factory = null)
    {
        var ctx = new ToolContext { SessionId = sessionId };
        if (factory is not null)
            ctx.Services = new StubServiceProvider().Add(factory);
        return ctx;
    }

    [Fact]
    public async Task Read_Should_RenderBlocks_WithHeadersAndFooter()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var m0 = await h.AddMessageAsync(root.Id, "user", "hello");
        var m1 = await h.AddMessageAsync(root.Id, "assistant", "world");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain($"[#0 | {m0.Id} | user | step=0 |");
        result.Output.Should().Contain("hello");
        result.Output.Should().Contain($"[#1 | {m1.Id} | assistant | step=0 |");
        result.Output.Should().Contain("world");
        result.Output.Should().Contain("(显示 1-2/共 2 条)");
        result.Metadata["total"].Should().Be(2);
        result.Metadata["from_index"].Should().Be(0);
        result.Metadata["to_index"].Should().Be(1);
        result.Metadata["truncated"].Should().Be(false);
        result.Metadata["next_cursor"].Should().BeNull();
    }

    [Fact]
    public async Task Read_FromTo_Should_ReturnClosedInterval()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "content-zero");
        var m1 = await h.AddMessageAsync(root.Id, "user", "content-one");
        await h.AddMessageAsync(root.Id, "user", "content-two");
        var m3 = await h.AddMessageAsync(root.Id, "user", "content-three");
        await h.AddMessageAsync(root.Id, "user", "content-four");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { from_id = m1.Id, to_id = m3.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("content-one").And.Contain("content-two").And.Contain("content-three");
        result.Output.Should().NotContain("content-zero").And.NotContain("content-four");
        result.Metadata["from_index"].Should().Be(1);
        result.Metadata["to_index"].Should().Be(3);
    }

    [Fact]
    public async Task Read_Window_Should_UseBeforeAndAfter()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "content-zero");
        await h.AddMessageAsync(root.Id, "user", "content-one");
        var m2 = await h.AddMessageAsync(root.Id, "user", "content-two");
        await h.AddMessageAsync(root.Id, "user", "content-three");
        await h.AddMessageAsync(root.Id, "user", "content-four");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { message_id = m2.Id, before = 1, after = 1 }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("content-one").And.Contain("content-two").And.Contain("content-three");
        result.Output.Should().NotContain("content-zero").And.NotContain("content-four");
    }

    [Fact]
    public async Task Read_Pagination_Should_Truncate_And_ExposeCursor()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "content-zero");
        await h.AddMessageAsync(root.Id, "user", "content-one");
        var m2 = await h.AddMessageAsync(root.Id, "user", "content-two");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { offset = 0, limit = 2 }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("content-zero").And.Contain("content-one").And.NotContain("content-two");
        result.Output.Should().Contain($"(显示 1-2/共 3 条；续读 from_id={m2.Id})");
        result.Metadata["truncated"].Should().Be(true);
        result.Metadata["next_cursor"].Should().Be(m2.Id);
    }

    [Fact]
    public async Task Read_Should_HideToolOutput_ByDefault()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var call = new SessionToolCall { Name = "bash", Status = "completed", Result = "SECRET_RESULT" };
        await h.AddMessageAsync(root.Id, "assistant", "", toolCalls: new List<SessionToolCall> { call });
        await h.AddMessageAsync(root.Id, "tool", "SECRET_RESULT", toolName: "bash");
        var tool = CreateTool(h);

        var hidden = await tool.ExecuteAsync(Args(new { }), Context(root.Id));
        hidden.Output.Should().Contain("tool: bash(completed)");
        hidden.Output.Should().NotContain("SECRET_RESULT");

        var shown = await tool.ExecuteAsync(
            Args(new { include_tool_output = true }), Context(root.Id));
        shown.Output.Should().Contain("SECRET_RESULT");
    }

    [Fact]
    public async Task Read_Should_TruncateContent_ByMaxChars()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", new string('x', 100));
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { max_chars = 10 }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("…[截断 90 字符]");
    }

    [Fact]
    public async Task Read_IncludeCompacted_Should_UseFullMessages()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "old-content");
        await h.AddMessageAsync(root.Id, "assistant", "summary-content", isSummary: true);
        await h.AddMessageAsync(root.Id, "user", "new-content");
        var tool = CreateTool(h);

        var active = await tool.ExecuteAsync(Args(new { }), Context(root.Id));
        active.Output.Should().NotContain("old-content");
        active.Output.Should().Contain("new-content");

        var full = await tool.ExecuteAsync(
            Args(new { include_compacted = true }), Context(root.Id));
        full.Output.Should().Contain("old-content");
    }

    [Fact]
    public async Task Read_SameGroup_Should_NotRequireAuthorization()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var child = await h.Groups.CreateChildAsync(
            root.Id, "build", "child", new List<SessionPermissionRule>(), null);
        await h.AddMessageAsync(child.Id, "user", "child-content");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = child.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("child-content");
    }

    [Fact]
    public async Task Read_CrossGroup_Should_RequireAuthorization()
    {
        using var h = new SessionToolTestHarness();
        var root1 = await h.CreateRootGroupedAsync("r1");
        var root2 = await h.CreateRootGroupedAsync("r2");
        await h.AddMessageAsync(root2.Id, "user", "private-content");
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Deny);
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root2.Id }), Context(root1.Id, factory));

        result.Success.Should().BeFalse();
        result.Output.Should().NotContain("private-content");
        factory.LastRequest.Should().NotBeNull();
        factory.LastRequest!.Resource.Should().Be("session_read");
    }

    [Fact]
    public async Task Read_Should_Fail_OnUnknownFromId()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "content-zero");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { from_id = "missing", to_id = "missing2" }), Context(root.Id));

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Read_Should_NotLeakReasoningContent()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "assistant", "visible", reasoning: "SECRET_REASONING");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(Args(new { }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("visible");
        result.Output.Should().NotContain("SECRET_REASONING");
    }
}
