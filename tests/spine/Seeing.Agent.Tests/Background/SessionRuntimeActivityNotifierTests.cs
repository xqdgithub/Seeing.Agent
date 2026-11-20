using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core.Background;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Background;

/// <summary>
/// <see cref="SessionRuntimeActivityNotifier"/> 的存在性闸与失败语义。
/// <para>
/// 目标会话不存在时必须抛 <see cref="BackgroundActivityTargetSessionMissingException"/>：
/// <c>SessionRuntime.DeliverAsync</c> 会为缺失的会话自动建空会话，不先判定就会「唤醒」已删除的会话。
/// </para>
/// </summary>
public class SessionRuntimeActivityNotifierTests
{
    [Fact]
    public async Task DeliverAsync_WithMissingSession_ShouldThrowWithoutDelivering()
    {
        var runtime = new Mock<ISessionRuntime>();
        var directory = new Mock<ISessionDirectory>();
        directory
            .Setup(d => d.ListSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionSummary>());
        directory
            .Setup(d => d.GetAsync("gone", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionData?)null);

        var notifier = new SessionRuntimeActivityNotifier(runtime.Object, directory.Object);

        await FluentActions
            .Awaiting(() => notifier.DeliverAsync(Message("gone")))
            .Should()
            .ThrowAsync<BackgroundActivityTargetSessionMissingException>()
            .WithMessage("*gone*");

        runtime.Verify(
            r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_WithExistingAnchor_ShouldBatchCheckThenDeliver()
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime
            .Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InboxResult.Ok("m1"));

        var directory = new Mock<ISessionDirectory>();
        directory
            .Setup(d => d.ListSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SessionSummary { Id = "s1" } });

        var notifier = new SessionRuntimeActivityNotifier(runtime.Object, directory.Object);
        var message = Message("s1");

        await notifier.DeliverAsync(message);

        runtime.Verify(
            r => r.DeliverAsync(message, It.IsAny<CancellationToken>()),
            Times.Once);

        // 命中摘要即不再按 id 单查（批量优先，避免逐条循环查目录）。
        directory.Verify(d => d.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_WithFailedInboxResult_ShouldThrow()
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime
            .Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InboxResult.Failed("runtime stopped"));

        var directory = new Mock<ISessionDirectory>();
        directory
            .Setup(d => d.ListSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SessionSummary { Id = "s1" } });

        var notifier = new SessionRuntimeActivityNotifier(runtime.Object, directory.Object);

        await FluentActions
            .Awaiting(() => notifier.DeliverAsync(Message("s1")))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime stopped*");
    }

    private static InboxMessage Message(string targetSessionId) => new()
    {
        TargetSessionId = targetSessionId,
        Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "background" },
        Body = "【后台活动完成】",
        Delivery = InboxDelivery.Wake,
        Presentation = InboxPresentation.System,
        SourceTag = BackgroundActivityOutbox.SourceTag,
        IdempotencyKey = "activity-digest:dgt_0123456789abcdef0123456789abcdef"
    };
}
