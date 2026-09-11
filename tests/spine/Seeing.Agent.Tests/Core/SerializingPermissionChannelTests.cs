using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class SerializingPermissionChannelTests
{
    [Fact]
    public async Task RequestAsync_ShouldSerializeConcurrentAsks()
    {
        var gate = new object();
        var concurrent = 0;
        var maxConcurrent = 0;

        var inner = new CountingChannel(() =>
        {
            lock (gate)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }

            Thread.Sleep(50);

            lock (gate)
            {
                concurrent--;
            }

            return PermissionChannelResult.Allowed();
        });

        var serial = new SerializingPermissionChannel(inner, new NoOpPermissionMemory());
        var request = new PermissionRequest
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            SessionId = "s1"
        };

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => serial.RequestAsync(request))
            .ToArray();

        await Task.WhenAll(tasks);

        maxConcurrent.Should().Be(1);
        inner.CallCount.Should().Be(4);
    }

    [Fact]
    public async Task RequestAsync_WhitelistedPathOutsideWorkspace_ShouldAllowWithoutInnerCall()
    {
        var inner = new CountingChannel(() => PermissionChannelResult.Allowed());
        var whitelist = new SessionWorkspaceWhitelist();
        whitelist.Add("s1", @"C:\data");

        var serial = new SerializingPermissionChannel(inner, new NoOpPermissionMemory(), whitelist: whitelist);
        var request = new PermissionRequest
        {
            PermissionKind = "filesystem.read",
            Resource = @"C:\data\file.txt",
            SessionId = "s1"
        };

        var result = await serial.RequestAsync(request);

        result.Action.Should().Be(PermissionChannelAction.Allow);
        inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Restrict_EmptySessionId_ShouldDenyWithoutInnerAsk()
    {
        var inner = new CountingChannel(() => PermissionChannelResult.Allowed());
        var options = CreateOptions(restrict: true);
        var serial = new SerializingPermissionChannel(
            inner, new NoOpPermissionMemory(), options: options);

        var result = await serial.RequestAsync(new PermissionRequest
        {
            PermissionKind = "filesystem.write",
            Resource = @"C:\outside\a.txt",
            SessionId = null
        });

        result.Action.Should().Be(PermissionChannelAction.Deny);
        result.Reason.Should().Contain("会话 ID");
        inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Restrict_AllowOutside_ShouldAddParentToWhitelist()
    {
        var inner = new CountingChannel(() => PermissionChannelResult.Allowed());
        var whitelist = new SessionWorkspaceWhitelist();
        var options = CreateOptions(restrict: true);
        var serial = new SerializingPermissionChannel(
            inner, new NoOpPermissionMemory(), whitelist: whitelist, options: options);

        var outsideFile = Path.Combine(Path.GetTempPath(), "seeing-boundary-tests", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideFile)!);

        var result = await serial.RequestAsync(new PermissionRequest
        {
            PermissionKind = "filesystem.write",
            Resource = outsideFile,
            SessionId = "s1"
        });

        result.Action.Should().Be(PermissionChannelAction.Allow);
        whitelist.Contains("s1", outsideFile).Should().BeTrue();
        inner.CallCount.Should().Be(1);

        // 再次请求应命中白名单，不再 Ask
        var again = await serial.RequestAsync(new PermissionRequest
        {
            PermissionKind = "filesystem.write",
            Resource = outsideFile,
            SessionId = "s1"
        });
        again.Action.Should().Be(PermissionChannelAction.Allow);
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Restrict_MemoryAllow_ShouldExpandWhitelistWithoutInnerAsk()
    {
        var inner = new CountingChannel(() => PermissionChannelResult.Denied("should not call"));
        var whitelist = new SessionWorkspaceWhitelist();
        var memory = new SessionPermissionMemory();
        var outsideDir = Path.Combine(Path.GetTempPath(), "seeing-boundary-mem");
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "x.txt");

        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "filesystem.write",
            Resource = outsideFile,
            Action = PermissionMemoryAction.Allow
        });

        var serial = new SerializingPermissionChannel(
            inner, memory, whitelist: whitelist, options: CreateOptions(restrict: true));

        var result = await serial.RequestAsync(new PermissionRequest
        {
            PermissionKind = "filesystem.write",
            Resource = outsideFile,
            SessionId = "s1"
        });

        result.Action.Should().Be(PermissionChannelAction.Allow);
        inner.CallCount.Should().Be(0);
        whitelist.Contains("s1", outsideFile).Should().BeTrue();
    }

    [Fact]
    public void Whitelist_ClearAll_ShouldRemoveAllSessions()
    {
        var whitelist = new SessionWorkspaceWhitelist();
        whitelist.Add("s1", @"C:\a");
        whitelist.Add("s2", @"C:\b");
        whitelist.ClearAll();
        whitelist.Contains("s1", @"C:\a\x").Should().BeFalse();
        whitelist.Contains("s2", @"C:\b\x").Should().BeFalse();
    }

    [Fact]
    public void PathGate_Restrict_Outside_ShouldReject()
    {
        var root = Path.Combine(Path.GetTempPath(), "seeing-gate-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new FakeWorkspace(root);
        var whitelist = new SessionWorkspaceWhitelist();
        var gate = new WorkspacePathGate(workspace, whitelist, CreateOptions(restrict: true));

        var outside = Path.Combine(Path.GetTempPath(), "seeing-gate-out-" + Guid.NewGuid().ToString("N"), "f.txt");
        gate.EnsureAllowed("s1", outside).Should().NotBeNullOrEmpty();
        gate.EnsureAllowed("s1", Path.Combine(root, "in.txt")).Should().BeNull();

        whitelist.Add("s1", Path.GetDirectoryName(outside)!);
        gate.EnsureAllowed("s1", outside).Should().BeNull();
    }

    private static IOptionsMonitor<SeeingAgentOptions> CreateOptions(bool restrict) =>
        new StaticOptionsMonitor(new SeeingAgentOptions
        {
            Workspace = new WorkspaceOptions { RestrictToWorkspace = restrict }
        });

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SeeingAgentOptions>
    {
        public StaticOptionsMonitor(SeeingAgentOptions value) => CurrentValue = value;
        public SeeingAgentOptions CurrentValue { get; }
        public SeeingAgentOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SeeingAgentOptions, string?> listener) => null;
    }

    private sealed class FakeWorkspace : IWorkspaceProvider
    {
        private readonly string _root;
        public FakeWorkspace(string root) => _root = root;
        public string UserSeeingDirectory => Path.Combine(_root, ".seeing");
        public string ProjectSeeingDirectory => Path.Combine(_root, ".seeing");
        public string StartupDirectory => _root;
        public WorkspaceResolutionSource ResolutionSource => WorkspaceResolutionSource.ManualSwitch;
        public string? GlobalWorkspaceRoot => null;
        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;
        public void SetWorkspaceRoot(string workspaceRoot) { }
        public string GetSeeingDirectory(ConfigLevel level) => Path.Combine(_root, ".seeing");
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingChannel : IPermissionChannel
    {
        private readonly Func<PermissionChannelResult> _onRequest;

        public CountingChannel(Func<PermissionChannelResult> onRequest) => _onRequest = onRequest;

        public int CallCount { get; private set; }

        public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_onRequest());
        }
    }

    private sealed class NoOpPermissionMemory : IPermissionMemory
    {
        public PermissionMemoryEntry? Match(string permissionKind, string? resource, string sessionId)
            => null;

        public void Remember(string sessionId, PermissionMemoryEntry entry) { }

        public void Forget(string sessionId, string? resource) { }

        public void ClearSession(string sessionId) { }

        public void ClearAll() { }
    }
}
