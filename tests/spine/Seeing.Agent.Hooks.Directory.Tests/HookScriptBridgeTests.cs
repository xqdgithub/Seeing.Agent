using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Hooks.Directory.Scripts;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookScriptBridgeTests
{
    private const string ScriptPath = @"E:\hooks\tool.execute.before.js";

    [Fact]
    public void Failure_AndCancel_ShouldMatchProtocol()
    {
        var failed = ScriptInvocationResult.Failure("boom");
        failed.Status.Should().Be(ScriptCallStatus.Failed);
        failed.Continue.Should().BeTrue();
        failed.Error.Should().Be("boom");
        failed.Mutable.Should().BeNull();

        var canceled = ScriptInvocationResult.Cancel();
        canceled.Status.Should().Be(ScriptCallStatus.Canceled);
        canceled.Mutable.Should().BeNull();
    }

    [Fact]
    public void HookJson_Options_ShouldNotRenameOrEscape()
    {
        HookJson.Options.PropertyNamingPolicy.Should().BeNull();
        HookJson.Options.Encoder.Should().BeSameAs(JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
    }

    [Fact]
    public async Task ExecuteAsync_InvokerFailed_ShouldContinueLeaveBagAndLogWarning()
    {
        var mutable = Bag(("proceed", true), ("marker", "original"));
        var input = new Dictionary<string, object?>
        {
            ["name"] = "<ok>",
            ["token"] = new object(),
        };
        var run = await RunAsync(ScriptInvocationResult.Failure("脚本抛错"), mutable, input);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        mutable["proceed"].Should().Be(true);
        mutable["marker"].Should().Be("original");
        run.Invoker.Calls.Should().Be(1);
        run.Invoker.LastScript.Should().Be(ScriptPath);
        run.Bridge.Spec.Should().Be(HookRegistry.ToolExecuteBefore);
        run.Bridge.Priority.Should().Be(-1000);

        using var evt = JsonDocument.Parse(run.Invoker.LastEventJson!);
        var root = evt.RootElement;
        root.GetProperty("point").GetString().Should().Be("tool.execute.before");
        root.GetProperty("sessionId").GetString().Should().Be("sess");
        root.GetProperty("messageId").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("step").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("input").GetProperty("name").GetString().Should().Be("<ok>");
        root.GetProperty("input").TryGetProperty("token", out _).Should().BeFalse();
        root.GetProperty("mutable").GetProperty("proceed").GetBoolean().Should().BeTrue();
        root.GetProperty("mutable").GetProperty("marker").GetString().Should().Be("original");
        root.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);
        run.Invoker.LastEventJson.Should().Contain("<ok>");
        run.Invoker.LastEventJson.Should().NotContain("\\u003C");

        run.Logger.Warnings.Should().ContainSingle();
        run.Logger.Warnings.Single().Should().Contain(ScriptPath).And.Contain("tool.execute.before");
        run.Logger.Debugs.Should().Contain(message => message.Contains("token"));
    }

    [Fact]
    public async Task ExecuteAsync_StopWithValidMutable_ShouldWriteAndSkipLaterHandler()
    {
        using var args = JsonDocument.Parse("{\"a\":1}");
        var mutable = new Dictionary<string, object?>
        {
            ["proceed"] = true,
            ["args"] = args.RootElement.Clone(),
            ["marker"] = "original",
        };
        var run = await RunAsync(
            Succeeded(false, "{\"proceed\":false,\"args\":{\"a\":2}}"),
            mutable);

        run.Result.Continue.Should().BeFalse();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(0);
        mutable["proceed"].Should().Be(false);
        mutable["args"].Should().BeOfType<JsonElement>().Which.GetProperty("a").GetInt32().Should().Be(2);
        mutable["marker"].Should().Be("original");
        mutable.ContainsKey("extra").Should().BeFalse();
        run.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_StopWithIllegalMutable_ShouldContinueAndLeaveBag()
    {
        var mutable = Bag(("proceed", true), ("marker", "original"));
        var run = await RunAsync(
            Succeeded(false, "{\"proceed\":\"no\",\"extra\":1}"),
            mutable);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        mutable["proceed"].Should().Be(true);
        mutable["marker"].Should().Be("original");
        mutable.ContainsKey("extra").Should().BeFalse();
        run.Logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(ScriptPath).And.Contain("tool.execute.before");
    }

    [Fact]
    public async Task ExecuteAsync_UndefinedArgsAndInvokerFailed_ShouldKeepUndefined()
    {
        var mutable = new Dictionary<string, object?>
        {
            ["args"] = default(JsonElement),
            ["marker"] = "original",
        };
        var run = await RunAsync(ScriptInvocationResult.Failure("投影后的失败"), mutable);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        mutable["args"].Should().BeOfType<JsonElement>().Which.ValueKind.Should().Be(JsonValueKind.Undefined);
        mutable["marker"].Should().Be("original");
        run.Invoker.Calls.Should().Be(1);

        using var evt = JsonDocument.Parse(run.Invoker.LastEventJson!);
        evt.RootElement.GetProperty("mutable").GetProperty("args").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ExecuteAsync_InvokerCanceled_ShouldSucceedWithoutWarning()
    {
        var mutable = Bag(("proceed", true));
        var run = await RunAsync(
            new ScriptInvocationResult(
                ScriptCallStatus.Canceled,
                Continue: false,
                Mutable: Json("{\"proceed\":false}"),
                Error: "canceled"),
            mutable);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        mutable["proceed"].Should().Be(true);
        run.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CanceledToken_ShouldForwardTokenWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mutable = Bag(("proceed", true));
        var invoker = new FakeNodeHookInvoker { Next = ScriptInvocationResult.Cancel() };
        var logger = new WarningLogger();
        var bridge = new HookScriptBridge(
            ScriptPath,
            new HookSpec(HookPolicy.FireAndForget, "tool.execute.before"),
            priority: -1000,
            invoker,
            logger);
        var probe = new ProbeHandler();
        var manager = new HookManager(NullLogger<HookManager>.Instance);
        manager.Register(bridge);
        manager.Register(probe);

        // 必须传入已经取消的令牌，不能换成测试自身的 CancellationToken。
#pragma warning disable xUnit1051
        var result = await manager.TriggerBlockingAsync(
            HookRegistry.ToolExecuteBefore,
            "sess",
            mutable: mutable,
            cancellationToken: cts.Token);
#pragma warning restore xUnit1051

        result.Continue.Should().BeTrue();
        result.Error.Should().BeNull();
        probe.Calls.Should().Be(1);
        invoker.Calls.Should().Be(1);
        invoker.LastToken.IsCancellationRequested.Should().BeTrue();
        mutable["proceed"].Should().Be(true);
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ProjectionThrows_ShouldSucceedWithoutInvoking()
    {
        var doc = JsonDocument.Parse("{\"a\":1}");
        var dead = doc.RootElement;
        doc.Dispose();
        var mutable = new Dictionary<string, object?>
        {
            ["args"] = dead,
            ["marker"] = "original",
        };

        var run = await RunAsync(ScriptInvocationResult.Failure("不应被调用"), mutable);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        run.Invoker.Calls.Should().Be(0);
        mutable["marker"].Should().Be("original");
        mutable["args"].Should().BeOfType<JsonElement>();
        run.Logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(ScriptPath).And.Contain("tool.execute.before");
    }

    [Fact]
    public async Task ExecuteAsync_InvokerThrowsCanceled_ShouldSucceedWithoutMutating()
    {
        var mutable = Bag(("proceed", true));
        var invoker = new FakeNodeHookInvoker { Fault = new OperationCanceledException("stopped") };
        var run = await RunAsync(invoker, mutable);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        mutable["proceed"].Should().Be(true);
        run.Logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(ScriptPath).And.Contain("tool.execute.before");
    }

    [Fact]
    public async Task ExecuteAsync_MutableNotObject_ShouldFailOpenEvenWhenStopping()
    {
        var mutable = Bag(("proceed", true));
        var run = await RunAsync(Succeeded(false, "[1,2]"), mutable);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        mutable["proceed"].Should().Be(true);
        run.Logger.Warnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task ExecuteAsync_StopWithoutWritableMutable_ShouldStopWithoutMutating(string? mutableJson)
    {
        var mutable = Bag(("proceed", true));
        var run = await RunAsync(Succeeded(false, mutableJson), mutable);

        run.Result.Continue.Should().BeFalse();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(0);
        mutable["proceed"].Should().Be(true);
        run.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UndefinedMutable_ShouldStopWithoutMutating()
    {
        var mutable = Bag(("proceed", true));
        var run = await RunAsync(
            new ScriptInvocationResult(ScriptCallStatus.Succeeded, false, default(JsonElement), null),
            mutable);

        run.Result.Continue.Should().BeFalse();
        run.Probe.Calls.Should().Be(0);
        mutable["proceed"].Should().Be(true);
        run.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ContinueWithValidMutable_ShouldWriteAndCallLaterHandler()
    {
        var mutable = Bag(("proceed", true), ("marker", "original"));
        var run = await RunAsync(Succeeded(true, "{\"proceed\":false}"), mutable);

        run.Result.Continue.Should().BeTrue();
        run.Result.Error.Should().BeNull();
        run.Probe.Calls.Should().Be(1);
        run.Probe.SawProceed.Should().Be(false);
        mutable["proceed"].Should().Be(false);
        mutable["marker"].Should().Be("original");
        run.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NullSessionId_ShouldSendEmptyString()
    {
        var logger = new WarningLogger();
        var invoker = new FakeNodeHookInvoker { Next = ScriptInvocationResult.Failure("x") };
        var bridge = new HookScriptBridge(ScriptPath, HookRegistry.ToolExecuteBefore, -1000, invoker, logger);
        var payload = new HookPayload
        {
            Spec = HookRegistry.ToolExecuteBefore,
            SessionId = null!,
            MessageId = "m",
            Step = 3,
            Mutable = Bag(("proceed", true)),
        };

        var result = await bridge.ExecuteAsync(payload);

        result.Continue.Should().BeTrue();
        result.Error.Should().BeNull();
        payload.Mutable["proceed"].Should().Be(true);
        using var evt = JsonDocument.Parse(invoker.LastEventJson!);
        evt.RootElement.GetProperty("sessionId").GetString().Should().Be("");
        evt.RootElement.GetProperty("messageId").GetString().Should().Be("m");
        evt.RootElement.GetProperty("step").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_FailedWithContinueFalse_ShouldIgnoreMutable()
    {
        var mutable = Bag(("proceed", true));
        var run = await RunAsync(
            new ScriptInvocationResult(ScriptCallStatus.Failed, false, Json("{\"proceed\":false}"), "nope"),
            mutable);

        run.Result.Continue.Should().BeTrue();
        run.Probe.Calls.Should().Be(1);
        mutable["proceed"].Should().Be(true);
        run.Logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void RecordingHookManager_Clear_ShouldIncrementEvenWhenEmpty()
    {
        var hooks = new RecordingHookManager();
        var handler = new ProbeHandler();
        hooks.Register(handler);
        hooks.RegisterMulti(new ProbeMulti());
        hooks.Count(HookRegistry.ToolExecuteBefore).Should().Be(2);

        hooks.Remove(handler).Should().BeTrue();
        hooks.Removed.Should().ContainSingle().Which.Should().BeSameAs(handler);
        hooks.Clear(HookRegistry.ToolExecuteBefore).Should().BeTrue();
        hooks.Clear(HookRegistry.ToolExecuteBefore).Should().BeFalse();

        hooks.ClearCount.Should().Be(2);
        hooks.Registered.Should().BeEmpty();
        hooks.RegisteredMulti.Should().BeEmpty();
        hooks.Version.Should().BeGreaterThan(0);
    }

    private static async Task<Run> RunAsync(
        ScriptInvocationResult next,
        Dictionary<string, object?> mutable,
        IReadOnlyDictionary<string, object?>? input = null)
    {
        var invoker = new FakeNodeHookInvoker { Next = next };
        return await RunAsync(invoker, mutable, input);
    }

    private static async Task<Run> RunAsync(
        FakeNodeHookInvoker invoker,
        Dictionary<string, object?> mutable,
        IReadOnlyDictionary<string, object?>? input = null)
    {
        var logger = new WarningLogger();
        var bridge = new HookScriptBridge(
            ScriptPath,
            new HookSpec(HookPolicy.FireAndForget, "tool.execute.before"),
            priority: -1000,
            invoker,
            logger);
        bridge.Spec.Should().Be(HookRegistry.ToolExecuteBefore);
        bridge.Priority.Should().Be(-1000);

        var probe = new ProbeHandler();
        var manager = new HookManager(NullLogger<HookManager>.Instance);
        manager.Register(bridge);
        manager.Register(probe);

        var result = await manager.TriggerBlockingAsync(
            HookRegistry.ToolExecuteBefore,
            "sess",
            input,
            mutable,
            TestContext.Current.CancellationToken);

        return new Run(result, probe, logger, invoker, bridge);
    }

    private static ScriptInvocationResult Succeeded(bool continueChain, string? mutableJson) =>
        new(ScriptCallStatus.Succeeded, continueChain, mutableJson is null ? null : Json(mutableJson), null);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, object?> Bag(params (string Key, object? Value)[] pairs)
    {
        var bag = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
            bag[key] = value;
        return bag;
    }

    private sealed record Run(
        HookResult Result,
        ProbeHandler Probe,
        WarningLogger Logger,
        FakeNodeHookInvoker Invoker,
        HookScriptBridge Bridge);

    private sealed class ProbeHandler : IHookHandler
    {
        public HookSpec Spec { get; init; } = HookRegistry.ToolExecuteBefore;
        public int Priority => 10;
        public int Calls { get; private set; }
        public object? SawProceed { get; private set; }

        public Task<HookResult> ExecuteAsync(HookPayload payload)
        {
            Calls++;
            payload.Mutable.TryGetValue("proceed", out var proceed);
            SawProceed = proceed;
            return Task.FromResult(HookResult.Success);
        }
    }

    private sealed class ProbeMulti : IMultiHookHandler
    {
        public IReadOnlyList<HookSpec> Specs { get; } = [HookRegistry.ToolExecuteBefore];
        public int Priority => 10;
        public Task<HookResult> ExecuteAsync(HookPayload payload) => Task.FromResult(HookResult.Success);
    }

    private sealed class FakeNodeHookInvoker : INodeHookInvoker
    {
        public ScriptInvocationResult Next { get; set; } = ScriptInvocationResult.Failure("unset");
        public Exception? Fault { get; set; }
        public int Calls { get; private set; }
        public string? LastScript { get; private set; }
        public string? LastEventJson { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public int StopCalls { get; private set; }

        public bool EnsureAvailable() => true;

        public Task<ScriptInvocationResult> InvokeAsync(
            string scriptPath,
            JsonElement eventPayload,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastScript = scriptPath;
            LastEventJson = eventPayload.GetRawText();
            LastToken = cancellationToken;
            if (Fault is not null)
                return Task.FromException<ScriptInvocationResult>(Fault);
            return Task.FromResult(Next);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public List<string> Debugs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
                Warnings.Add(message);
            else if (logLevel == LogLevel.Debug)
                Debugs.Add(message);
        }
    }
}
