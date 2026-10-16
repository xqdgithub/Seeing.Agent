using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class HostRequestDispatcherTests
{
    private const string Session = "s1";
    private const string Circuit = "c1";
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(10);

    private static HostRequest Pending(string requestId, string sessionId = Session)
        => new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            Kind = "editor.command"
        };

    [Fact]
    public async Task LocalPanel_ShouldInvokeHandlerAndResolveCompleted()
    {
        var manager = new FakeHostRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new HostRequestInbox(manager);
        var panels = new HostRequestActivePanelRegistry();

        var invoked = 0;
        panels.Register(Session, Circuit, (req, _) =>
        {
            invoked++;
            return Task.FromResult(new HostResponse
            {
                RequestId = req.RequestId,
                SessionId = req.SessionId,
                Status = HostResponseStatus.Completed
            });
        });

        using var dispatcher = new HostRequestDispatcher(inbox, panels, manager, Circuit, Delay);
        await WaitUntilAsync(() => manager.Resolves.Count >= 1);

        invoked.Should().Be(1);
        manager.Resolves.Should().ContainSingle()
            .Which.Response.Status.Should().Be(HostResponseStatus.Completed);
        manager.Resolves[0].RequestId.Should().Be("r1");
        manager.Resolves[0].ExpectedSessionId.Should().Be(Session);
    }

    [Fact]
    public async Task OtherCircuitPanel_ShouldSkipWithoutResolving()
    {
        var manager = new FakeHostRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new HostRequestInbox(manager);
        var panels = new HostRequestActivePanelRegistry();

        panels.Register(Session, "other-circuit", (req, _) =>
            Task.FromResult(new HostResponse
            {
                RequestId = req.RequestId,
                SessionId = req.SessionId,
                Status = HostResponseStatus.Completed
            }));

        using var dispatcher = new HostRequestDispatcher(inbox, panels, manager, Circuit, Delay);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        manager.Resolves.Should().BeEmpty();
    }

    [Fact]
    public async Task NoPanelAnywhere_ShouldResolveUnavailable()
    {
        var manager = new FakeHostRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new HostRequestInbox(manager);
        var panels = new HostRequestActivePanelRegistry();

        using var dispatcher = new HostRequestDispatcher(inbox, panels, manager, Circuit, Delay);
        await WaitUntilAsync(() => manager.Resolves.Count >= 1);

        manager.Resolves.Should().ContainSingle()
            .Which.Response.Status.Should().Be(HostResponseStatus.Unavailable);
    }

    [Fact]
    public async Task EmptyCircuitId_ShouldNotSubscribeOrDispatch()
    {
        var manager = new FakeHostRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new HostRequestInbox(manager);
        var panels = new HostRequestActivePanelRegistry();

        panels.Register(Session, string.Empty, (req, _) =>
            Task.FromResult(new HostResponse
            {
                RequestId = req.RequestId,
                SessionId = req.SessionId,
                Status = HostResponseStatus.Completed
            }));

        using var dispatcher = new HostRequestDispatcher(inbox, panels, manager, string.Empty, Delay);

        manager.Seed(Pending("r2"));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        manager.Resolves.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlerThrows_ShouldResolveFailed()
    {
        var manager = new FakeHostRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new HostRequestInbox(manager);
        var panels = new HostRequestActivePanelRegistry();

        panels.Register(Session, Circuit, (_, _) => throw new InvalidOperationException("boom"));

        using var dispatcher = new HostRequestDispatcher(inbox, panels, manager, Circuit, Delay);
        await WaitUntilAsync(() => manager.Resolves.Count >= 1);

        var resolve = manager.Resolves.Single();
        resolve.Response.Status.Should().Be(HostResponseStatus.Failed);
        resolve.Response.Reason.Should().Be("boom");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>测试替身：在途宿主请求管理器（记录 TryResolve 调用；PendingChanged 可手动触发）。</summary>
    private sealed class FakeHostRequestManager : IHostRequestManager
    {
        private readonly Dictionary<string, HostRequest> _pending = new(StringComparer.Ordinal);

        public List<(string RequestId, HostResponse Response, string? ExpectedSessionId)> Resolves { get; } = new();

        public event Action? PendingChanged;

        public void Seed(params HostRequest[] requests)
        {
            foreach (var request in requests)
            {
                if (!string.IsNullOrEmpty(request.RequestId))
                    _pending[request.RequestId] = request;
            }
            PendingChanged?.Invoke();
        }

        public Task<RequestTicket> BeginAsync(HostRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<HostResponse> WaitAsync(RequestTicket ticket, CancellationToken ct = default)
            => throw new NotSupportedException();

        public bool TryResolve(string requestId, HostResponse response, string? expectedSessionId = null)
        {
            Resolves.Add((requestId, response, expectedSessionId));
            _pending.Remove(requestId);
            return true;
        }

        public IReadOnlyList<HostRequest> GetPending(string sessionId)
            => string.IsNullOrEmpty(sessionId)
                ? Array.Empty<HostRequest>()
                : _pending.Values
                    .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                    .ToList();

        public IReadOnlyList<HostRequest> GetAllPending() => _pending.Values.ToList();

        public int PendingCount => _pending.Count;

        public void Dispose()
        {
        }
    }
}
