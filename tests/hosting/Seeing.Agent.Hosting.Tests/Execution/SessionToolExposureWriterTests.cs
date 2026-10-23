using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

/// <summary>
/// 会话暴露状态唯一写出口：提升 / 提交 / 会话缺失语义。
/// </summary>
public class SessionToolExposureWriterTests
{
    private static (SessionToolExposureWriter Writer, SessionData Session) Create()
    {
        var session = SessionData.Create();
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.Get(session.Id)).Returns(session);
        return (new SessionToolExposureWriter(
            manager.Object, NullLogger<SessionToolExposureWriter>.Instance), session);
    }

    [Fact]
    public void PromotePending_ShouldMovePendingIntoDiscovered()
    {
        var (writer, session) = Create();
        session.ToolExposure.AddPending(["tool_a"]);

        writer.PromotePending(session);

        session.ToolExposure.GetDiscovered().Should().Equal("tool_a");
        session.ToolExposure.GetPending().Should().BeEmpty();
    }

    [Fact]
    public void Commit_ShouldWriteDiscoveryMetricsAndEscalation()
    {
        var (writer, session) = Create();

        writer.Commit(session, ["tool_a"], escalated: true, schemaTokens: 12, catalogTokens: 34);

        session.ToolExposure.GetDiscovered().Should().Equal("tool_a");
        session.ToolExposure.Escalated.Should().BeTrue();
        session.ToolExposure.SchemaTokens.Should().Be(12);
        session.ToolExposure.CatalogTokens.Should().Be(34);
    }

    [Fact]
    public async Task PromotePendingAsync_MissingSession_ShouldReturnFalse_NotThrow()
    {
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.TryGetOrLoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionData?)null);
        var writer = new SessionToolExposureWriter(
            manager.Object, NullLogger<SessionToolExposureWriter>.Instance);

        var promoted = await writer.PromotePendingAsync(
            "missing", TestContext.Current.CancellationToken);

        promoted.Should().BeFalse();
    }

    [Fact]
    public async Task PromotePendingAsync_ShouldLoadAndPromote()
    {
        var session = SessionData.Create();
        session.ToolExposure.AddPending(["tool_a"]);
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.TryGetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var writer = new SessionToolExposureWriter(
            manager.Object, NullLogger<SessionToolExposureWriter>.Instance);

        var promoted = await writer.PromotePendingAsync(
            session.Id, TestContext.Current.CancellationToken);

        promoted.Should().BeTrue();
        session.ToolExposure.GetDiscovered().Should().Equal("tool_a");
    }
}
