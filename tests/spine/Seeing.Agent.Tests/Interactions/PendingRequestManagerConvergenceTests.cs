using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;
using Xunit;

namespace Seeing.Agent.Tests.Interactions;

/// <summary>
/// 泛型在途请求管理器基类呈现端注销收敛测试（spec §4.4）：
/// 仅注销触发收敛；不可呈现会话的在途请求以 Unavailable 兜底；仍可呈现或仅内容收缩不收敛。
/// </summary>
public class PendingRequestManagerConvergenceTests
{
    [Fact]
    public async Task ProviderUnregistered_NoLongerSurfaceable_ShouldConvergeToUnavailable()
    {
        var surface = new FakeSurface();
        surface.SetSurface("s1");
        using var manager = new TestManager(surface, TimeSpan.FromMilliseconds(50));

        var ticket = await manager.BeginAsync(new Req { SessionId = "s1" }, TestContext.Current.CancellationToken);

        surface.SetSurface();
        surface.FireUnregistered();

        await WaitUntilAsync(() => manager.PendingCount == 0);

        manager.PendingCount.Should().Be(0);
        var result = await manager.WaitAsync(ticket, TestContext.Current.CancellationToken);
        result.RequestId.Should().Be(ticket.RequestId);
        result.Status.Should().Be(nameof(PendingFallbackReason.Unavailable));
    }

    [Fact]
    public async Task ProviderUnregistered_StillSurfaceable_ShouldNotConverge()
    {
        var surface = new FakeSurface();
        surface.SetSurface("s1");
        using var manager = new TestManager(surface, TimeSpan.FromMilliseconds(50));

        var ticket = await manager.BeginAsync(new Req { SessionId = "s1" }, TestContext.Current.CancellationToken);

        surface.FireUnregistered();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        manager.PendingCount.Should().Be(1);
        manager.TryResolve(ticket.RequestId, new Res(ticket.RequestId) { Status = "resolved" }).Should().BeTrue();
    }

    [Fact]
    public async Task SurfaceShrinkWithoutUnregister_ShouldNotConverge()
    {
        var surface = new FakeSurface();
        surface.SetSurface("s1");
        using var manager = new TestManager(surface, TimeSpan.FromMilliseconds(50));

        var ticket = await manager.BeginAsync(new Req { SessionId = "s1" }, TestContext.Current.CancellationToken);

        surface.SetSurface();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        manager.PendingCount.Should().Be(1);
        manager.TryResolve(ticket.RequestId, new Res(ticket.RequestId) { Status = "resolved" }).Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
            await Task.Delay(10);
        condition().Should().BeTrue("条件应在超时前满足");
    }

    private sealed class Req
    {
        public string Id { get; set; } = "";

        public string SessionId { get; set; } = "";
    }

    private sealed class Res
    {
        public Res(string requestId) => RequestId = requestId;

        public string RequestId { get; }

        public string Status { get; set; } = "";
    }

    private sealed class TestManager : PendingRequestManager<Req, Res>
    {
        public TestManager(ISurfaceRegistry? registry, TimeSpan grace) : base(null, registry, grace)
        {
        }

        protected override string GetRequestId(Req r) => r.Id;

        protected override Req AssignRequestId(Req r, string id)
        {
            r.Id = id;
            return r;
        }

        protected override string GetSessionId(Req r) => r.SessionId;

        protected override Res CreateFallback(Req r, PendingFallbackReason reason) =>
            new(r.Id) { Status = reason.ToString() };

        protected override Res CreateMissingResponse(RequestTicket t) =>
            new(t.RequestId) { Status = "missing" };
    }

    private sealed class FakeSurface : ISurfaceRegistry
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

        public void Register(ISurfaceProvider p) { }

        public void Unregister(ISurfaceProvider p) { }

        public bool CanSurface(string sessionId) => _ids.Contains(sessionId);

        public event Action? ProviderUnregistered;

        public event Action? Changed;

        public void SetSurface(params string[] ids)
        {
            _ids.Clear();
            foreach (var i in ids)
                _ids.Add(i);
            Changed?.Invoke();
        }

        public void FireUnregistered() => ProviderUnregistered?.Invoke();
    }
}
