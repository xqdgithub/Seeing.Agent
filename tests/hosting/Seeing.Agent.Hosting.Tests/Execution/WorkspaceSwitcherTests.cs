using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

public class WorkspaceSwitcherTests
{
    [Fact]
    public async Task Apply_WithoutInFlight_ShouldSkipCancel_AndFollowOrder()
    {
        using var harness = new Harness(hasInFlight: false);

        var result = await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.NewRoot),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        harness.Runtime.Verify(r => r.CancelAllInFlightAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Workspace.Verify(w => w.SetWorkspaceRoot(harness.NewRoot), Times.Once);
        harness.History.Verify(h => h.RecordOpened(harness.NewRoot, true), Times.Once);
        harness.Participant.Applied.Should().Be(1);
        harness.Participant.Applying.Should().Be(1);
        harness.Events.Should().Equal("applying", "reload", "applied");
    }

    [Fact]
    public async Task Apply_WithInFlight_ShouldRejectPermissions_ThenCancel_ThenWaitIdle_BeforeSetRoot()
    {
        using var harness = new Harness(hasInFlight: true);

        await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.NewRoot),
            TestContext.Current.CancellationToken);

        harness.Permissions.Verify(p => p.GetAllPending(), Times.Once);
        harness.Runtime.Verify(r => r.CancelAllInFlightAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Runtime.Verify(r => r.WaitUntilIdleAsync(It.IsAny<CancellationToken>()), Times.Once);

        var cancelIndex = harness.Calls.IndexOf("cancel");
        var idleIndex = harness.Calls.IndexOf("wait-idle");
        var setRootIndex = harness.Calls.IndexOf("set-root");
        cancelIndex.Should().BeLessThan(idleIndex);
        idleIndex.Should().BeLessThan(setRootIndex, "改根必须等执行真的停下来");
    }

    [Fact]
    public async Task Apply_WhenReloadReportsFailure_ShouldRollBack_AndNotRecordHistory()
    {
        using var harness = new Harness(hasInFlight: false);
        harness.Reloader.Results =
            [new ReloadResult { ComponentId = "session", Success = false, Error = "boom" }];

        var result = await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.NewRoot),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("session");
        harness.Workspace.Verify(w => w.SetWorkspaceRoot(harness.OldRoot), Times.Once, "失败要回滚旧根");
        harness.History.Verify(h => h.RecordOpened(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        harness.Participant.Applied.Should().Be(0);
    }

    [Fact]
    public async Task Apply_ToSameRoot_ShouldDoNothing()
    {
        using var harness = new Harness(hasInFlight: false);

        var result = await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.OldRoot),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        harness.Workspace.Verify(w => w.SetWorkspaceRoot(It.IsAny<string>()), Times.Never);
        harness.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_ToMissingDirectory_ShouldFailWithoutTouchingWorkspace()
    {
        using var harness = new Harness(hasInFlight: false);

        var result = await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(Path.Combine(harness.Root, "gone")),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        harness.Workspace.Verify(w => w.SetWorkspaceRoot(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Apply_Should_ReportProgress_FromBeginToEnd()
    {
        using var harness = new Harness(hasInFlight: true);

        await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.NewRoot),
            TestContext.Current.CancellationToken);

        harness.Progress.Messages.Should().NotBeEmpty("切换过程中必须持续上报，否则界面像卡死");
        harness.Progress.Messages.First().Should().Contain("正在");
        harness.Progress.Begun.Should().BeTrue();
        harness.Progress.Ended.Should().BeTrue("结束后必须关闭覆盖层");
    }

    private sealed class Harness : IDisposable
    {
        public Harness(bool hasInFlight)
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-switch-" + Guid.NewGuid().ToString("N"));
            OldRoot = Path.Combine(Root, "old");
            NewRoot = Path.Combine(Root, "new");
            Directory.CreateDirectory(OldRoot);
            Directory.CreateDirectory(NewRoot);

            Workspace.Setup(w => w.IsResolved).Returns(true);
            Workspace.Setup(w => w.ResolvedRoot).Returns(OldRoot);
            Workspace.Setup(w => w.SetWorkspaceRoot(It.IsAny<string>()))
                .Callback<string>(p => Calls.Add("set-root"));

            Runtime.Setup(r => r.HasInFlight()).Returns(hasInFlight);
            Runtime.Setup(r => r.CancelAllInFlightAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("cancel"))
                .ReturnsAsync(1);
            Runtime.Setup(r => r.WaitUntilIdleAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("wait-idle"))
                .Returns(Task.CompletedTask);

            Permissions.Setup(p => p.GetAllPending()).Returns(Array.Empty<PermissionRequest>());

            Reloader.OnReload = () => Events.Add("reload");

            Participant = new RecordingParticipant(Events);
            Switcher = new WorkspaceSwitcher(
                Workspace.Object,
                Config,
                History.Object,
                Reloader,
                Runtime.Object,
                Permissions.Object,
                new[] { Participant },
                NullLogger<WorkspaceSwitcher>.Instance,
                Progress);
        }

        public string Root { get; }
        public string OldRoot { get; }
        public string NewRoot { get; }
        public Mock<IWorkspaceProvider> Workspace { get; } = new();
        public Mock<IExecutionInFlightBoundary> Runtime { get; } = new();
        public Mock<IPermissionRequestManager> Permissions { get; } = new();
        public Mock<IWorkspaceHistoryStore> History { get; } = new();
        public FakeConfigReloader Config { get; } = new();
        public FakeReloader Reloader { get; } = new();
        public RecordingParticipant Participant { get; }
        public WorkspaceSwitcher Switcher { get; }
        public RecordingProgress Progress { get; } = new();
        public List<string> Calls { get; } = new();
        public List<string> Events { get; } = new();

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    // 切换会真的把进程当前目录挪到新根，删之前先挪出来
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                    Directory.Delete(Root, true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeConfigReloader : IWorkspaceConfigReloader
    {
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingProgress : IWorkspaceSwitchProgress
    {
        public List<string> Messages { get; } = new();
        public bool Begun { get; private set; }
        public bool Ended { get; private set; }

        public void Begin(string message)
        {
            Begun = true;
            Messages.Add(message);
        }

        public void Report(string message) => Messages.Add(message);

        public void End() => Ended = true;
    }

    private sealed class FakeReloader : IWorkspaceReloadInvoker
    {
        public IReadOnlyList<ReloadResult> Results { get; set; } = Array.Empty<ReloadResult>();
        public Action? OnReload { get; set; }

        public Task<IReadOnlyList<ReloadResult>> ReloadAsync(WorkspaceApplyContext ctx, CancellationToken ct)
        {
            OnReload?.Invoke();
            return Task.FromResult(Results);
        }
    }

    private sealed class RecordingParticipant : IWorkspaceApplyParticipant
    {
        private readonly List<string> _events;

        public RecordingParticipant(List<string> events) => _events = events;

        public int Applying { get; private set; }
        public int Applied { get; private set; }
        public string ComponentId => "recording";

        public Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
        {
            Applying++;
            _events.Add("applying");
            return Task.CompletedTask;
        }

        public Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
        {
            Applied++;
            _events.Add("applied");
            return Task.CompletedTask;
        }
    }
}
