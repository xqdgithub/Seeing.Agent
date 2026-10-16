using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Cli.Services;
using Seeing.Session.Core;
using System.Runtime.CompilerServices;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class OneShotRunnerTests
{
    private static SessionData NewSession(string id) => new()
    {
        Id = id,
        Title = "t",
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now,
    };

    private sealed class Fixture
    {
        public Mock<ISessionRuntime> Runtime { get; } = new();
        public Mock<ISessionDirectory> Directory { get; } = new();
        public Mock<ISessionManager> SessionManager { get; } = new();
        public StringWriter Stdout { get; } = new();
        public StringWriter Stderr { get; } = new();
        public List<InboxMessage> Delivered { get; } = new();
        public List<(string sessionId, SessionAutoApprove mode)> AutoApproveCalls { get; } = new();

        public Fixture()
        {
            Directory.Setup(d => d.CreateAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => NewSession("ses_test"));

            SessionManager
                .Setup(m => m.SetAutoApproveAsync(It.IsAny<string>(), It.IsAny<SessionAutoApprove>(), It.IsAny<CancellationToken>()))
                .Callback<string, SessionAutoApprove, CancellationToken>((id, mode, _) => AutoApproveCalls.Add((id, mode)))
                .Returns(Task.CompletedTask);

            Runtime.Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
                .Callback<InboxMessage, CancellationToken>((m, _) => Delivered.Add(m))
                .ReturnsAsync(InboxResult.Ok("msg_1"));

            Runtime.Setup(r => r.WaitForIdleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        public void Emit(params IMessageEvent[] events)
        {
            Runtime.Setup(r => r.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(StreamAsync(events));
        }

        public OneShotRunner Create(bool stream = false)
            => new(Runtime.Object, Directory.Object, SessionManager.Object,
                new OneShotOutput(Stdout, Stderr, stream));

        private static async IAsyncEnumerable<IMessageEvent> StreamAsync(
            IMessageEvent[] events,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task RunAsync_WhenCompleted_ShouldReturnZeroAndPrintFinalText()
    {
        var fx = new Fixture();
        fx.Emit(
            new StreamCompleteEvent { SessionId = "ses_test", Message = new ChatMessage { Content = "结果" } },
            new ExecutionCompleteEvent { SessionId = "ses_test", ExecutionId = "exec_1", Status = ExecutionStatus.Completed });

        var code = await fx.Create().RunAsync(new OneShotOptions("你好"), TestContext.Current.CancellationToken);

        code.Should().Be(0);
        fx.Stdout.ToString().Should().Contain("结果");
    }

    [Fact]
    public async Task RunAsync_WhenFailed_ShouldReturnOne()
    {
        var fx = new Fixture();
        fx.Emit(new ExecutionCompleteEvent
        {
            SessionId = "ses_test",
            ExecutionId = "exec_1",
            Status = ExecutionStatus.Failed
        });

        var code = await fx.Create().RunAsync(new OneShotOptions("你好"), TestContext.Current.CancellationToken);

        code.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WithYolo_ShouldSetSessionAutoApproveEnabled()
    {
        var fx = new Fixture();
        fx.Emit(new ExecutionCompleteEvent { SessionId = "ses_test", ExecutionId = "exec_1", Status = ExecutionStatus.Completed });

        await fx.Create().RunAsync(new OneShotOptions("你好", Yolo: true), TestContext.Current.CancellationToken);

        fx.AutoApproveCalls.Should().ContainSingle();
        fx.AutoApproveCalls[0].mode.Should().Be(SessionAutoApprove.Enabled);
    }

    [Fact]
    public async Task RunAsync_WithoutYolo_ShouldNotSetAutoApprove()
    {
        var fx = new Fixture();
        fx.Emit(new ExecutionCompleteEvent { SessionId = "ses_test", ExecutionId = "exec_1", Status = ExecutionStatus.Completed });

        await fx.Create().RunAsync(new OneShotOptions("你好"), TestContext.Current.CancellationToken);

        fx.AutoApproveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ShouldDeliverUserPresentationAndForwardAgentModel()
    {
        var fx = new Fixture();
        fx.Emit(new ExecutionCompleteEvent { SessionId = "ses_test", ExecutionId = "exec_1", Status = ExecutionStatus.Completed });

        await fx.Create().RunAsync(
            new OneShotOptions("你好", Agent: "plan", Model: "openai/gpt-4o"),
            TestContext.Current.CancellationToken);

        fx.Delivered.Should().ContainSingle();
        var msg = fx.Delivered[0];
        msg.TargetSessionId.Should().Be("ses_test");
        msg.Presentation.Should().Be(InboxPresentation.User);
        msg.Delivery.Should().Be(InboxDelivery.Wake);
        msg.Body.Should().Be("你好");
        msg.AgentId.Should().Be("plan");
        msg.ModelId.Should().Be("openai/gpt-4o");
    }

    [Fact]
    public async Task RunAsync_WhenDeliveryFails_ShouldReturnOne()
    {
        var fx = new Fixture();
        fx.Runtime.Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InboxResult.Failed("nope"));

        var code = await fx.Create().RunAsync(new OneShotOptions("你好"), TestContext.Current.CancellationToken);

        code.Should().Be(1);
        fx.Stderr.ToString().Should().Contain("nope");
    }

    [Fact]
    public async Task RunAsync_WhenErrorEvent_ShouldReturnOne()
    {
        var fx = new Fixture();
        fx.Emit(
            new ErrorEvent { SessionId = "ses_test", Message = "llm down" },
            new ExecutionCompleteEvent { SessionId = "ses_test", ExecutionId = "exec_1", Status = ExecutionStatus.Completed });

        var code = await fx.Create().RunAsync(new OneShotOptions("你好"), TestContext.Current.CancellationToken);

        code.Should().Be(1);
    }
}
