using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Hooks.Directory.Scripts;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookDirectoryServiceTests : IDisposable
{
    private readonly string _user;
    private readonly string _project;
    private readonly string _outside;

    public HookDirectoryServiceTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _user = Path.Combine(Path.GetTempPath(), "seeing-hook-user-" + id);
        _project = Path.Combine(Path.GetTempPath(), "seeing-hook-project-" + id);
        _outside = Path.Combine(Path.GetTempPath(), "seeing-hook-outside-" + id);
        System.IO.Directory.CreateDirectory(_user);
        System.IO.Directory.CreateDirectory(_project);
        System.IO.Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _user, _project, _outside })
        {
            try
            {
                if (System.IO.Directory.Exists(root))
                    System.IO.Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 不回收的加载上下文会锁住 hooks/.cache 里的影子副本。
            }
        }
    }

    [Fact]
    public async Task SynchronizeAsync_ProjectMirror_ShouldRegisterUserBridgeThenRemoveSameInstance()
    {
        var userScript = WriteJs(_user, "tool.execute.before/audit.js");
        var projectScript = WriteJs(_project, "tool.execute.before/audit.js");
        var hooks = new RecordingHookManager();
        var invoker = new FakeInvoker { Available = true };
        var service = Create(hooks, invoker);

        await service.SynchronizeAsync(TestContext.Current.CancellationToken);

        hooks.ClearCount.Should().Be(0);
        var bridge = hooks.Registered.Should().ContainSingle().Which.Should().BeOfType<HookScriptBridge>().Subject;
        await bridge.ExecuteAsync(Payload());
        var seen = invoker.LastScript;
        seen.Should().NotBeNull();
        Path.GetFullPath(seen).Should().Be(Path.GetFullPath(userScript));
        Path.GetFullPath(seen).Should().NotBe(Path.GetFullPath(projectScript));
        invoker.EnsureCalls.Should().Be(1);

        File.Delete(userScript);
        await service.SynchronizeAsync(TestContext.Current.CancellationToken);

        hooks.ClearCount.Should().Be(0);
        hooks.Removed.Should().ContainSingle().Which.Should().BeSameAs(bridge);
        hooks.Registered.Should().BeEmpty();
        invoker.EnsureCalls.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_Disabled_ShouldNotRegisterOrProbe()
    {
        WriteJs(_user, "tool.execute.before/audit.js");
        var hooks = new RecordingHookManager();
        var invoker = new FakeInvoker { Available = true };
        var service = Create(hooks, invoker, enabled: false);

        await service.StartAsync(TestContext.Current.CancellationToken);

        hooks.Registered.Should().BeEmpty();
        hooks.RegisteredMulti.Should().BeEmpty();
        hooks.ClearCount.Should().Be(0);
        invoker.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task SynchronizeAsync_NodeUnavailable_ShouldRegisterDllHandlersOnly()
    {
        var hooksDir = Path.Combine(_user, "hooks");
        System.IO.Directory.CreateDirectory(hooksDir);
        File.Copy(FixturePaths.HandlerDll(), Path.Combine(hooksDir, "sample.dll"));
        WriteJs(_user, "tool.execute.before.js");
        var hooks = new RecordingHookManager();
        var invoker = new FakeInvoker { Available = false };
        var service = Create(hooks, invoker);

        await service.SynchronizeAsync(TestContext.Current.CancellationToken);

        invoker.EnsureCalls.Should().Be(1);
        hooks.ClearCount.Should().Be(0);
        hooks.Registered.Should().NotContain(handler => handler is HookScriptBridge);
        hooks.Registered.Select(handler => handler.GetType().Name)
            .Should().BeEquivalentTo("SampleHook", "ThrowingHook");
        hooks.RegisteredMulti.Select(handler => handler.GetType().Name)
            .Should().ContainSingle().Which.Should().Be("MultiAndSingleHook");
    }

    [Fact]
    public async Task StopAsync_ShouldRemoveOnlyDirectoryBridge()
    {
        WriteJs(_user, "tool.execute.before/audit.js");
        var hooks = new HookManager(NullLogger<HookManager>.Instance);
        var sentinel = new SentinelHook();
        hooks.Register(sentinel);
        var invoker = new FakeInvoker { Available = true };
        var service = Create(hooks, invoker);
        var ct = TestContext.Current.CancellationToken;

        await service.StartAsync(ct);

        hooks.Count(HookRegistry.ToolExecuteBefore).Should().Be(2);

        await service.StopAsync(ct);

        hooks.Count(HookRegistry.ToolExecuteBefore).Should().Be(1);
        await hooks.TriggerBlockingAsync(HookRegistry.ToolExecuteBefore, "sess", cancellationToken: ct);
        sentinel.Calls.Should().Be(1);
        invoker.InvokeCalls.Should().Be(0);
        invoker.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task SynchronizeAsync_SymlinkOutsideRoot_ShouldNotRegister()
    {
        var outsideFile = Path.Combine(_outside, "outside.js");
        File.WriteAllText(outsideFile, "");
        var hooksDir = Path.Combine(_user, "hooks");
        System.IO.Directory.CreateDirectory(hooksDir);
        var link = Path.Combine(hooksDir, "tool.execute.before.js");
        File.CreateSymbolicLink(link, outsideFile);

        var hooks = new RecordingHookManager();
        var invoker = new FakeInvoker { Available = true };
        var service = Create(hooks, invoker);

        await service.SynchronizeAsync(TestContext.Current.CancellationToken);

        hooks.Registered.Should().BeEmpty();
        hooks.RegisteredMulti.Should().BeEmpty();
        hooks.ClearCount.Should().Be(0);
        invoker.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task SynchronizeAsync_MissingRoot_ShouldNotCreateHooksDirectory()
    {
        var hooks = new RecordingHookManager();
        var invoker = new FakeInvoker { Available = true };
        var service = Create(hooks, invoker);

        await service.SynchronizeAsync(TestContext.Current.CancellationToken);

        System.IO.Directory.Exists(Path.Combine(_user, "hooks")).Should().BeFalse();
        hooks.Registered.Should().BeEmpty();
        hooks.ClearCount.Should().Be(0);
        invoker.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_SecondCall_ShouldIgnoreLaterDeletion()
    {
        var userScript = WriteJs(_user, "tool.execute.before/audit.js");
        var hooks = new RecordingHookManager();
        var invoker = new FakeInvoker { Available = true };
        var service = Create(hooks, invoker);
        var ct = TestContext.Current.CancellationToken;

        await service.StartAsync(ct);
        File.Delete(userScript);
        await service.StartAsync(ct);

        hooks.Registered.Should().ContainSingle().Which.Should().BeOfType<HookScriptBridge>();
        hooks.Removed.Should().BeEmpty();
        hooks.ClearCount.Should().Be(0);
        invoker.EnsureCalls.Should().Be(1);
    }

    private HookDirectoryService Create(IHookManager hooks, FakeInvoker invoker, bool enabled = true) =>
        new(
            hooks,
            new FakeDirectories(_user, _project),
            Options.Create(new HookDirectoryOptions { Enabled = enabled }),
            invoker,
            NullLogger<HookDirectoryService>.Instance);

    private static string WriteJs(string seeingRoot, string relative)
    {
        var path = Path.Combine(seeingRoot, "hooks", relative.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private HookPayload Payload() =>
        HookPayload.Blocking(
            HookRegistry.ToolExecuteBefore,
            "sess",
            cancellationToken: TestContext.Current.CancellationToken);

    private sealed class FakeDirectories(string user, string project) : ISeeingDirectories
    {
        public string UserSeeingDirectory { get; } = user;
        public string ProjectSeeingDirectory { get; } = project;
    }

    private sealed class SentinelHook : IHookHandler
    {
        public HookSpec Spec => HookRegistry.ToolExecuteBefore;
        public int Priority => 10;
        public int Calls { get; private set; }

        public Task<HookResult> ExecuteAsync(HookPayload payload)
        {
            Calls++;
            return Task.FromResult(HookResult.Success);
        }
    }

    private sealed class FakeInvoker : INodeHookInvoker
    {
        public bool Available { get; set; } = true;
        public int EnsureCalls { get; private set; }
        public int InvokeCalls { get; private set; }
        public int StopCalls { get; private set; }
        public string? LastScript { get; private set; }

        public bool EnsureAvailable()
        {
            EnsureCalls++;
            return Available;
        }

        public Task<ScriptInvocationResult> InvokeAsync(
            string scriptPath,
            System.Text.Json.JsonElement eventPayload,
            CancellationToken cancellationToken)
        {
            InvokeCalls++;
            LastScript = scriptPath;
            return Task.FromResult(ScriptInvocationResult.Failure("unused"));
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
    }
}
