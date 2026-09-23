using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Core.Interactions;
using Seeing.Agent.Core.Questions;
using Xunit;

namespace Seeing.Agent.Tests.Questions;

/// <summary>
/// 问答在途管理器测试（spec §6.2 / §9）：幂等决议、超时/取消、缺失 ticket、呈现端注销收敛、事件发布。
/// </summary>
public class QuestionRequestManagerTests
{
    [Fact]
    public async Task WaitAsync_TryResolve_ShouldReturnSubmittedResult_AndBeIdempotent()
    {
        using var harness = new Harness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        var submitted = new QuestionResult
        {
            RequestId = ticket.RequestId,
            Status = QuestionResultStatus.Completed,
            Answers = new List<QuestionAnswer>
            {
                new() { QuestionId = "q1", SelectedLabels = new List<string> { "A" } }
            }
        };

        harness.Manager.TryResolve(ticket.RequestId, submitted).Should().BeTrue();
        harness.Manager.TryResolve(ticket.RequestId, submitted).Should().BeFalse();

        var result = await waiter;
        result.Status.Should().Be(QuestionResultStatus.Completed);
        result.RequestId.Should().Be(ticket.RequestId);
        result.Answers.Should().ContainSingle(a => a.QuestionId == "q1");
    }

    [Fact]
    public async Task WaitAsync_Timeout_ShouldReturnTimeoutStatus()
    {
        using var harness = new Harness(timeout: TimeSpan.FromMilliseconds(80));
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var result = await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        result.Status.Should().Be(QuestionResultStatus.Timeout);
        result.RequestId.Should().Be(ticket.RequestId);
    }

    [Fact]
    public async Task WaitAsync_Cancelled_ShouldReturnCancelledStatus()
    {
        using var harness = new Harness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var waiter = harness.Manager.WaitAsync(ticket, cts.Token);
        cts.Cancel();

        var result = await waiter;
        result.Status.Should().Be(QuestionResultStatus.Cancelled);
    }

    [Fact]
    public async Task WaitAsync_UnknownTicket_ShouldReturnUnavailable()
    {
        using var harness = new Harness();

        var result = await harness.Manager.WaitAsync(new RequestTicket("missing", "s1"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(QuestionResultStatus.Unavailable);
        result.RequestId.Should().Be("missing");
    }

    [Fact]
    public async Task ProviderUnregistered_ShouldConvergePendingToUnavailable()
    {
        using var harness = new Harness(convergenceGrace: TimeSpan.FromMilliseconds(50));
        var provider = new TestProvider("s1");
        harness.Registry.Register(provider);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        harness.Registry.Unregister(provider);

        var result = await waiter;
        result.Status.Should().Be(QuestionResultStatus.Unavailable);
        result.RequestId.Should().Be(ticket.RequestId);
    }

    [Fact]
    public async Task ProviderUnregistered_OtherSession_ShouldNotConverge()
    {
        using var harness = new Harness(convergenceGrace: TimeSpan.FromMilliseconds(50));
        var s1Provider = new TestProvider("s1");
        var otherProvider = new TestProvider("other");
        harness.Registry.Register(s1Provider);
        harness.Registry.Register(otherProvider);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        // 注销的是另一个会话的呈现端；s1 仍有可呈现提供方，不应收敛
        harness.Registry.Unregister(otherProvider);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        harness.Manager.PendingCount.Should().Be(1);

        harness.Manager.TryResolve(ticket.RequestId, new QuestionResult
        {
            RequestId = ticket.RequestId,
            Status = QuestionResultStatus.Completed
        }).Should().BeTrue();
    }

    [Fact]
    public async Task BeginAndResolve_ShouldPublishEvents()
    {
        using var harness = new Harness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var request = harness.PublishedEvents.OfType<QuestionRequestEvent>().Single();
        request.RequestId.Should().Be(ticket.RequestId);
        request.SessionId.Should().Be("s1");
        request.CallId.Should().Be("call-1");
        request.Questions.Should().ContainSingle(q => q.Id == "q1");

        harness.Manager.TryResolve(ticket.RequestId, new QuestionResult
        {
            RequestId = ticket.RequestId,
            Status = QuestionResultStatus.Completed,
            Answers = new List<QuestionAnswer> { new() { QuestionId = "q1" } }
        });

        var resolved = harness.PublishedEvents.OfType<QuestionResolvedEvent>().Single();
        resolved.RequestId.Should().Be(ticket.RequestId);
        resolved.SessionId.Should().Be("s1");
        resolved.CallId.Should().Be("call-1");
        resolved.Status.Should().Be(QuestionResultStatus.Completed);
        resolved.Answers.Should().ContainSingle(a => a.QuestionId == "q1");
    }

    [Fact]
    public async Task Dispose_ShouldReturnUnavailable_AndUnsubscribe()
    {
        var harness = new Harness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        var provider = new TestProvider("s1");
        harness.Registry.Register(provider);

        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);
        harness.Manager.Dispose();

        var result = await waiter;
        result.Status.Should().Be(QuestionResultStatus.Unavailable);

        // 已退订：注销不再触发收敛（Dispose 幂等亦安全）
        harness.Registry.Unregister(provider);
        var act = () => harness.Manager.Dispose();
        act.Should().NotThrow();
    }

    private static QuestionRequest NewRequest(string sessionId) => new()
    {
        SessionId = sessionId,
        Tool = new ToolReference { MessageId = "m1", CallId = "call-1" },
        Questions = new List<Question>
        {
            new() { Id = "q1", Header = "选择", QuestionText = "请选择" }
        }
    };

    private sealed class Harness : IDisposable
    {
        public List<IMessageEvent> PublishedEvents { get; } = new();
        public SurfaceRegistry Registry { get; } = new();
        public QuestionRequestManager Manager { get; }

        public Harness(TimeSpan? timeout = null, TimeSpan? convergenceGrace = null)
        {
            var publisher = new Mock<IExecutionEventPublisher>();
            publisher
                .Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
                .Callback<string, IMessageEvent>((_, evt) => PublishedEvents.Add(evt));

            Manager = new QuestionRequestManager(
                publisher.Object,
                Registry,
                NullLogger<QuestionRequestManager>.Instance,
                timeout ?? TimeSpan.FromMinutes(10),
                convergenceGrace);
        }

        public void Dispose() => Manager.Dispose();
    }

    private sealed class TestProvider : ISurfaceProvider
    {
        private IReadOnlyCollection<string> _surface;

        public TestProvider(params string[] sessionIds) => _surface = sessionIds;

        public IReadOnlyCollection<string> SurfaceSessionIds => _surface;

        public event Action? SurfacedChanged;

        public void SetSurface(params string[] sessionIds)
        {
            _surface = sessionIds;
            SurfacedChanged?.Invoke();
        }
    }
}
