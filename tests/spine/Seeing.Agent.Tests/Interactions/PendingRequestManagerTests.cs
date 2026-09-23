using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;
using Xunit;

namespace Seeing.Agent.Tests.Interactions;

/// <summary>泛型在途请求管理器基类测试（spec §5.2）：Begin/Wait/TryResolve 幂等、超时/取消/释放、归属校验、即时完成、订阅者隔离。</summary>
public class PendingRequestManagerTests
{
    [Fact]
    public async Task BeginAsync_ShouldAssignRequestId_AndTrackPending()
    {
        using var manager = new TestManager();
        var request = new TestRequest { SessionId = "s1" };

        var ticket = await manager.BeginAsync(request, TestContext.Current.CancellationToken);

        ticket.RequestId.Should().NotBeNullOrEmpty();
        ticket.SessionId.Should().Be("s1");
        manager.PendingCount.Should().Be(1);
        manager.GetPending("s1").Should().ContainSingle();
        manager.GetAllPending().Should().ContainSingle();
    }

    [Fact]
    public async Task BeginAsync_ShouldPreserveCallerProvidedRequestId()
    {
        using var manager = new TestManager();

        var ticket = await manager.BeginAsync(new TestRequest { Id = "fixed", SessionId = "s1" }, TestContext.Current.CancellationToken);

        ticket.RequestId.Should().Be("fixed");
    }

    [Fact]
    public async Task GetPending_EmptySessionId_ShouldReturnEmpty()
    {
        using var manager = new TestManager();
        await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        manager.GetPending("").Should().BeEmpty();
    }

    [Fact]
    public async Task WaitAsync_ShouldReturnResolvedResponse()
    {
        using var manager = new TestManager();
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        var waiter = manager.WaitAsync(ticket, TestContext.Current.CancellationToken);
        var resolved = new TestResponse(42);

        manager.TryResolve(ticket.RequestId, resolved).Should().BeTrue();
        (await waiter).Should().BeSameAs(resolved);
        manager.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task TryResolve_SecondCall_ShouldReturnFalse()
    {
        using var manager = new TestManager();
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        manager.TryResolve(ticket.RequestId, new TestResponse(42)).Should().BeTrue();
        manager.TryResolve(ticket.RequestId, new TestResponse(99)).Should().BeFalse();
    }

    [Fact]
    public async Task TryResolve_ShouldValidateExpectedSessionId()
    {
        using var manager = new TestManager();
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        manager.TryResolve(ticket.RequestId, new TestResponse(42), "other").Should().BeFalse();
        manager.TryResolve(ticket.RequestId, new TestResponse(42), "s1").Should().BeTrue();
    }

    [Fact]
    public async Task WaitAsync_Timeout_ShouldReturnTimeoutFallback()
    {
        using var manager = new TestManager(TimeSpan.FromMilliseconds(50));
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        (await manager.WaitAsync(ticket, TestContext.Current.CancellationToken)).Should().BeSameAs(TestManager.TimeoutValue);
    }

    [Fact]
    public async Task WaitAsync_CancelledToken_ShouldReturnCancelledFallback()
    {
        using var manager = new TestManager();
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        (await manager.WaitAsync(ticket, cts.Token)).Should().BeSameAs(TestManager.CancelledValue);
    }

    [Fact]
    public async Task WaitAsync_MissingTicket_ShouldReturnMissingResponse()
    {
        using var manager = new TestManager();

        (await manager.WaitAsync(new RequestTicket("nope", "s1"), TestContext.Current.CancellationToken)).Should().BeSameAs(TestManager.MissingValue);
    }

    [Fact]
    public async Task ValidateOnBegin_NonNull_ShouldCompleteImmediatelyWithoutOnRequestBegan()
    {
        var immediate = new TestResponse(7);
        using var manager = new TestManager { ImmediateResult = immediate };
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        (await manager.WaitAsync(ticket, TestContext.Current.CancellationToken)).Should().BeSameAs(immediate);
        manager.BeganCalled.Should().BeFalse();
        manager.ResolvedCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateOnBegin_Null_ShouldCallOnRequestBegan()
    {
        using var manager = new TestManager();
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        manager.BeganCalled.Should().BeTrue();
        manager.TryResolve(ticket.RequestId, new TestResponse(1));
        manager.ResolvedCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_ShouldResolveInFlightWithDisposedFallback()
    {
        var manager = new TestManager();
        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);
        var waiter = manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        manager.Dispose();

        (await waiter).Should().BeSameAs(TestManager.DisposedValue);
        var act = () => manager.BeginAsync(new TestRequest { SessionId = "s1" });
        await act.Should().ThrowAsync<ObjectDisposedException>();
        manager.Dispose();
    }

    [Fact]
    public async Task Dispose_ShouldInvokeOnDisposingHook()
    {
        var manager = new TestManager();
        await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);

        manager.Dispose();

        manager.DisposingCalled.Should().BeTrue();
    }

    [Fact]
    public async Task PendingChanged_ShouldBeRaised_AndIsolateSubscriberExceptions()
    {
        using var manager = new TestManager();
        var count = 0;
        manager.PendingChanged += () => throw new InvalidOperationException("boom");
        manager.PendingChanged += () => count++;

        var ticket = await manager.BeginAsync(new TestRequest { SessionId = "s1" }, TestContext.Current.CancellationToken);
        manager.TryResolve(ticket.RequestId, new TestResponse(42));

        count.Should().BeGreaterThan(0);
    }

    private sealed class TestRequest
    {
        public string Id { get; set; } = "";

        public string SessionId { get; set; } = "";
    }

    private sealed record TestResponse(int Value);

    private sealed class TestManager : PendingRequestManager<TestRequest, TestResponse>
    {
        public static readonly TestResponse MissingValue = new(-999);
        public static readonly TestResponse TimeoutValue = new(-1);
        public static readonly TestResponse CancelledValue = new(-2);
        public static readonly TestResponse DisposedValue = new(-3);

        private readonly TimeSpan? _timeout;

        public TestManager(TimeSpan? timeout = null)
        {
            _timeout = timeout;
        }

        public TestResponse? ImmediateResult { get; set; }

        public bool BeganCalled { get; private set; }

        public bool ResolvedCalled { get; private set; }

        public bool DisposingCalled { get; private set; }

        protected override TimeSpan Timeout => _timeout ?? base.Timeout;

        protected override string GetRequestId(TestRequest request) => request.Id;

        protected override TestRequest AssignRequestId(TestRequest request, string id)
        {
            request.Id = id;
            return request;
        }

        protected override string GetSessionId(TestRequest request) => request.SessionId;

        protected override TestResponse CreateFallback(TestRequest request, PendingFallbackReason reason) => reason switch
        {
            PendingFallbackReason.Timeout => TimeoutValue,
            PendingFallbackReason.Cancelled => CancelledValue,
            PendingFallbackReason.Disposed => DisposedValue,
            _ => new TestResponse(-4)
        };

        protected override TestResponse CreateMissingResponse(RequestTicket ticket) => MissingValue;

        protected override TestResponse? ValidateOnBegin(TestRequest request) => ImmediateResult;

        protected override void OnRequestBegan(TestRequest request) => BeganCalled = true;

        protected override void OnResolved(TestRequest request, TestResponse response) => ResolvedCalled = true;

        protected override void OnDisposing() => DisposingCalled = true;
    }
}
