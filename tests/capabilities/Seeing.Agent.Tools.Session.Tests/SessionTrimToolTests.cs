using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tools.Session.Tests;

public class SessionTrimToolTests
{
    private static SessionTrimTool CreateTool(SessionToolTestHarness h) =>
        new(NullLogger<SessionTrimTool>.Instance, h.Sessions, h.Groups);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(string sessionId) => new() { SessionId = sessionId };

    private static List<string> MessageIds(SessionData session) =>
        session.Messages.Select(m => m.Id!).ToList();

    [Fact]
    public async Task Trim_Before_Should_RemoveEarlierTrimmableMessages_And_Backup()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var u0 = await h.AddMessageAsync(root.Id, "user", "old user");
        var a0 = await h.AddMessageAsync(root.Id, "assistant", "old assistant");
        var u1 = await h.AddMessageAsync(root.Id, "user", "middle user");
        var a1 = await h.AddMessageAsync(root.Id, "assistant", "middle assistant");
        var u2 = await h.AddMessageAsync(root.Id, "user", "last user");
        var a2 = await h.AddMessageAsync(root.Id, "assistant", "tail assistant");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "before", message_id = a1.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Metadata["removed_count"].Should().Be(3);
        result.Metadata["backup_session_id"].Should().NotBeNull();
        result.Metadata["remaining_active_count"].Should().Be(3);

        var ids = MessageIds(await h.Sessions.GetOrLoadAsync(root.Id, TestContext.Current.CancellationToken));
        ids.Should().NotContain(u0.Id!).And.NotContain(a0.Id!).And.NotContain(u1.Id!);
        ids.Should().Contain(a1.Id!).And.Contain(u2.Id!).And.Contain(a2.Id!);

        var group = await h.Groups.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var members = await h.Groups.ListMembersAsync(group!.Id, TestContext.Current.CancellationToken);
        members.Should().Contain(m => m.Relation == SessionRelation.Fork);
    }

    [Fact]
    public async Task Trim_Range_Should_RemoveOnlyInterval()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var u0 = await h.AddMessageAsync(root.Id, "user", "u0");
        await h.AddMessageAsync(root.Id, "assistant", "a0");
        await h.AddMessageAsync(root.Id, "user", "u1");
        var a1 = await h.AddMessageAsync(root.Id, "assistant", "a1");
        var u2 = await h.AddMessageAsync(root.Id, "user", "last user");
        var a2 = await h.AddMessageAsync(root.Id, "assistant", "a2");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "range", from_id = u0.Id, to_id = a1.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Metadata["removed_count"].Should().Be(4);

        var ids = MessageIds(await h.Sessions.GetOrLoadAsync(root.Id, TestContext.Current.CancellationToken));
        ids.Should().Equal(u2.Id!, a2.Id!);
    }

    [Fact]
    public async Task Trim_Should_NeverRemoveSummary()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var summary = await h.AddMessageAsync(root.Id, "assistant", "summary-content", isSummary: true);
        var u1 = await h.AddMessageAsync(root.Id, "user", "u1");
        var a1 = await h.AddMessageAsync(root.Id, "assistant", "a1");
        var u2 = await h.AddMessageAsync(root.Id, "user", "last user");
        await h.AddMessageAsync(root.Id, "assistant", "a2");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "before", message_id = u2.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Metadata["removed_count"].Should().Be(2);

        var ids = MessageIds(await h.Sessions.GetOrLoadAsync(root.Id, TestContext.Current.CancellationToken));
        ids.Should().Contain(summary.Id!).And.Contain(u2.Id!);
        ids.Should().NotContain(u1.Id!).And.NotContain(a1.Id!);
    }

    [Fact]
    public async Task Trim_Should_ProtectLastUserAndAfter()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "assistant", "summary-content", isSummary: true);
        var u1 = await h.AddMessageAsync(root.Id, "user", "u1");
        var a1 = await h.AddMessageAsync(root.Id, "assistant", "a1");
        var u2 = await h.AddMessageAsync(root.Id, "user", "last user");
        var a2 = await h.AddMessageAsync(root.Id, "assistant", "a2");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "range", from_id = u2.Id, to_id = a2.Id }), Context(root.Id));

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();

        var ids = MessageIds(await h.Sessions.GetOrLoadAsync(root.Id, TestContext.Current.CancellationToken));
        ids.Should().Contain(u1.Id!).And.Contain(a1.Id!).And.Contain(u2.Id!).And.Contain(a2.Id!);
    }

    [Fact]
    public async Task Trim_Should_PreserveCompactedHistory()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var old = await h.AddMessageAsync(root.Id, "user", "compacted-old");
        await h.AddMessageAsync(root.Id, "assistant", "summary-content", isSummary: true);
        var u1 = await h.AddMessageAsync(root.Id, "user", "u1");
        var a1 = await h.AddMessageAsync(root.Id, "assistant", "a1");
        var u2 = await h.AddMessageAsync(root.Id, "user", "last user");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "before", message_id = u2.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        var ids = MessageIds(await h.Sessions.GetOrLoadAsync(root.Id, TestContext.Current.CancellationToken));
        ids.Should().Contain(old.Id!);
        ids.Should().NotContain(u1.Id!).And.NotContain(a1.Id!);
    }

    [Fact]
    public async Task Trim_After_Should_RemoveLaterTrimmableMessages()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var u0 = await h.AddMessageAsync(root.Id, "user", "u0");
        var a0 = await h.AddMessageAsync(root.Id, "assistant", "a0");
        var a1 = await h.AddMessageAsync(root.Id, "assistant", "a1");
        var u1 = await h.AddMessageAsync(root.Id, "user", "last user");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "after", message_id = u0.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        result.Metadata["removed_count"].Should().Be(2);

        var ids = MessageIds(await h.Sessions.GetOrLoadAsync(root.Id, TestContext.Current.CancellationToken));
        ids.Should().Equal(u0.Id!, u1.Id!);
    }

    [Fact]
    public async Task Trim_Should_FailClosed_WhenBackupFails()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var u0 = await h.AddMessageAsync(root.Id, "user", "old user");
        var a0 = await h.AddMessageAsync(root.Id, "assistant", "old assistant");
        await h.AddMessageAsync(root.Id, "user", "u1");
        var a1 = await h.AddMessageAsync(root.Id, "assistant", "a1");
        await h.AddMessageAsync(root.Id, "user", "last user");

        var groups = new Mock<ISessionGroupManager>();
        groups.Setup(g => g.CreateBackupForkAsync(root.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new IOException("backup failed"));
        var tool = new SessionTrimTool(NullLogger<SessionTrimTool>.Instance, h.Sessions, groups.Object);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "before", message_id = a1.Id }), Context(root.Id));

        result.Success.Should().BeFalse();
        var ids = MessageIds(await h.Sessions.GetOrLoadAsync(root.Id, TestContext.Current.CancellationToken));
        ids.Should().Contain(u0.Id!).And.Contain(a0.Id!);
    }

    [Fact]
    public async Task Trim_Should_Fail_OnUnknownMode()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "u0");
        var tool = CreateTool(h);

        var result = await tool.ExecuteAsync(
            Args(new { mode = "bogus", message_id = "x" }), Context(root.Id));

        result.Success.Should().BeFalse();
    }
}
