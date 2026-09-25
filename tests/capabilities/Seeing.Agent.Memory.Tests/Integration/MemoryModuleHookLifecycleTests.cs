using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Hosting;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

/// <summary>
/// Memory Hook 收编到模块 Activate/Deactivate 的生命周期对称测试。
/// </summary>
public class MemoryModuleHookLifecycleTests
{
    [Fact]
    public async Task 模块启用登记Hook_停用撤销_不再写入()
    {
        var hooks = new RecordingHookManager();
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor());

        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var services = new ServiceCollection();
        services.AddSingleton<IHookManager>(hooks);
        services.AddSingleton<ISessionMemoryBuffer>(buffer);
        services.AddSingleton(new Mock<IMemoryFlushService>().Object);
        services.AddSingleton(filter.Object);
        services.AddSingleton(new Mock<IMemoryRecallService>().Object);
        services.AddSingleton<ISessionActivityTracker>(new SessionActivityTracker());
        services.AddSingleton(MemoryTestOptions.Monitor());
        services.AddSingleton<IOptions<MemoryOptions>>(Options.Create(new MemoryOptions()));
        services.AddSingleton<ChatMemoryHandler>();
        services.AddSingleton<ToolMemoryHandler>();
        services.AddSingleton<AgentTurnMemoryHandler>();
        services.AddSingleton<MemoryRecallHandler>();

        var module = new MemoryModule(new MemoryModuleActivity(), new SqliteConnectionOwner(() => "Data Source=:memory:"));
        await using var provider = services.BuildServiceProvider();
        var ct = TestContext.Current.CancellationToken;

        await module.ActivateAsync(provider, ct);
        hooks.Count(HookRegistry.ChatAfterComplete).Should().Be(1);
        hooks.Count(HookRegistry.ChatBeforeStart).Should().Be(1);

        await hooks.TriggerAsync(ChatPayload("session-1"));
        buffer.GetPendingCount("session-1").Should().Be(1);

        await module.DeactivateAsync(provider, ct);
        hooks.Count(HookRegistry.ChatAfterComplete).Should().Be(0);
        hooks.Count(HookRegistry.ChatBeforeStart).Should().Be(0);

        await hooks.TriggerAsync(ChatPayload("session-1"));
        buffer.GetPendingCount("session-1").Should().Be(1, "停用后 Hook 已移除，不应再写入缓冲");
    }

    [Fact]
    public async Task 启用停用再启用_Hook登记幂等()
    {
        var hooks = new RecordingHookManager();
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var services = new ServiceCollection();
        services.AddSingleton<IHookManager>(hooks);
        services.AddSingleton<ISessionMemoryBuffer>(new SessionMemoryBuffer(MemoryTestOptions.Monitor()));
        services.AddSingleton(new Mock<IMemoryFlushService>().Object);
        services.AddSingleton(filter.Object);
        services.AddSingleton(new Mock<IMemoryRecallService>().Object);
        services.AddSingleton<ISessionActivityTracker>(new SessionActivityTracker());
        services.AddSingleton(MemoryTestOptions.Monitor());
        services.AddSingleton<IOptions<MemoryOptions>>(Options.Create(new MemoryOptions()));
        services.AddSingleton<ChatMemoryHandler>();
        services.AddSingleton<ToolMemoryHandler>();
        services.AddSingleton<AgentTurnMemoryHandler>();
        services.AddSingleton<MemoryRecallHandler>();

        var module = new MemoryModule(new MemoryModuleActivity(), new SqliteConnectionOwner(() => "Data Source=:memory:"));
        await using var provider = services.BuildServiceProvider();
        var ct = TestContext.Current.CancellationToken;

        await module.ActivateAsync(provider, ct);
        await module.DeactivateAsync(provider, ct);
        await module.ActivateAsync(provider, ct);

        hooks.Count(HookRegistry.ChatAfterComplete).Should().Be(1);
        hooks.Count(HookRegistry.ToolExecuteAfter).Should().Be(1);
        hooks.Count(HookRegistry.AgentAfterInvoke).Should().Be(1);
        hooks.Count(HookRegistry.ChatBeforeStart).Should().Be(1);
    }

    private static HookPayload ChatPayload(string sessionId) =>
        HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            sessionId,
            result: new Dictionary<string, object?> { ["content"] = "用户偏好使用深色主题，并要求默认语言为中文。" },
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>最小可执行的 IHookManager 替身：按点登记并真实执行处理器。</summary>
    private sealed class RecordingHookManager : IHookManager
    {
        private readonly Dictionary<string, List<object>> _handlers = new();

        public void Register(IHookHandler handler) => Add(handler.Spec.Point, handler);

        public void RegisterMulti(IMultiHookHandler handler)
        {
            foreach (var spec in handler.Specs)
                Add(spec.Point, handler);
        }

        public bool Remove(IHookHandler handler)
        {
            if (!_handlers.TryGetValue(handler.Spec.Point, out var list))
                return false;
            return list.Remove(handler);
        }

        public bool Clear(HookSpec spec) => _handlers.Remove(spec.Point);

        public int Count(HookSpec spec) => _handlers.TryGetValue(spec.Point, out var list) ? list.Count : 0;

        public async Task<HookResult> TriggerAsync(HookPayload payload)
        {
            if (_handlers.TryGetValue(payload.Spec.Point, out var list))
            {
                foreach (var handler in list.OfType<IHookHandler>())
                    await handler.ExecuteAsync(payload);
            }
            return HookResult.Success;
        }

        public Task<HookResult> TriggerBlockingAsync(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            IDictionary<string, object?>? mutable = null,
            CancellationToken cancellationToken = default)
            => TriggerAsync(HookPayload.Blocking(spec, sessionId, input, mutable, cancellationToken));

        public void TriggerFireAndForget(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            IReadOnlyDictionary<string, object?>? result = null)
            => _ = TriggerAsync(HookPayload.FireAndForget(spec, sessionId, input, result));

        public Task TriggerParallelAsync(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            CancellationToken cancellationToken = default)
            => TriggerAsync(HookPayload.Parallel(spec, sessionId, input, cancellationToken));

        private void Add(string point, object handler)
        {
            if (!_handlers.TryGetValue(point, out var list))
                _handlers[point] = list = new List<object>();
            list.Add(handler);
        }
    }
}
