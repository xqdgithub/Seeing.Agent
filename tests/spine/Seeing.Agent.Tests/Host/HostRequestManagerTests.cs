using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Host;
using Seeing.Agent.Core.Interactions;
using Xunit;

namespace Seeing.Agent.Tests.Host;

/// <summary>
/// 宿主请求在途管理器测试：幂等决议、超时/取消、缺失 ticket、呈现端注销收敛。
/// </summary>
public class HostRequestManagerTests
{
    [Fact]
    public async Task WaitAsync_TryResolve_ShouldReturnPayload_AndBeIdempotent()
    {
        using var harness = new Harness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        var submitted = new HostResponse
        {
            RequestId = ticket.RequestId,
            SessionId = "s1",
            Status = HostResponseStatus.Completed
        };

        harness.Manager.TryResolve(ticket.RequestId, submitted).Should().BeTrue();
        harness.Manager.TryResolve(ticket.RequestId, submitted).Should().BeFalse();

        var result = await waiter;
        result.Status.Should().Be(HostResponseStatus.Completed);
        result.RequestId.Should().Be(ticket.RequestId);
    }

    [Fact]
    public async Task WaitAsync_Timeout_ShouldReturnTimeout()
    {
        using var harness = new Harness(timeout: TimeSpan.FromMilliseconds(50));
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);

        var result = await harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        result.Status.Should().Be(HostResponseStatus.Timeout);
        result.RequestId.Should().Be(ticket.RequestId);
    }

    [Fact]
    public async Task WaitAsync_Cancelled_ShouldReturnCancelled()
    {
        using var harness = new Harness();
        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var waiter = harness.Manager.WaitAsync(ticket, cts.Token);
        cts.Cancel();

        var result = await waiter;
        result.Status.Should().Be(HostResponseStatus.Cancelled);
    }

    [Fact]
    public async Task WaitAsync_UnknownTicket_ShouldReturnUnavailable()
    {
        using var harness = new Harness();

        var result = await harness.Manager.WaitAsync(new RequestTicket("missing", "s1"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HostResponseStatus.Unavailable);
        result.RequestId.Should().Be("missing");
    }

    [Fact]
    public async Task ProviderUnregistered_ShouldConvergeToUnavailable()
    {
        using var harness = new Harness(convergenceGrace: TimeSpan.FromMilliseconds(50));
        var provider = new TestProvider("s1");
        harness.Registry.Register(provider);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"), TestContext.Current.CancellationToken);
        var waiter = harness.Manager.WaitAsync(ticket, TestContext.Current.CancellationToken);

        harness.Registry.Unregister(provider);

        var result = await waiter;
        result.Status.Should().Be(HostResponseStatus.Unavailable);
        result.RequestId.Should().Be(ticket.RequestId);
    }

    private static HostRequest NewRequest(string sessionId) => new()
    {
        SessionId = sessionId,
        Kind = "editor.query"
    };

    private sealed class Harness : IDisposable
    {
        public SurfaceRegistry Registry { get; } = new();
        public HostRequestManager Manager { get; }

        public Harness(TimeSpan? timeout = null, TimeSpan? convergenceGrace = null)
        {
            Manager = new HostRequestManager(
                Registry,
                timeout ?? TimeSpan.FromSeconds(30),
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
