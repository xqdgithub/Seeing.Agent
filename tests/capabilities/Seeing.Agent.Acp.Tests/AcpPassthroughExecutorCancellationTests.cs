using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Mapping;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

/// <summary>
/// ACP 透传执行器取消路径：ReadAllAsync 的 OCE 不得逃逸迭代器，
/// 必须产出 <see cref="LoopCancelledEvent"/> 终态。
/// </summary>
public class AcpPassthroughExecutorCancellationTests
{
    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ShouldEmitLoopCancelledEvent()
    {
        // Arrange：会话运行器阻塞直至取消，模拟后端挂起
        var executor = CreateExecutor(new BlockingSessionRunner());
        var context = new AgentContext { SessionId = "sess-cancel" };
        var messages = new List<ChatMessage> { new() { Role = "user", Content = "hi" } };
        var agent = new AgentDefinition
        {
            Name = "acp-opencode",
            Runtime = AgentRuntime.AcpPassthrough,
            AcpBackend = "opencode"
        };

        using var cts = new CancellationTokenSource();
        var events = new List<IMessageEvent>();

        // Act：在收到 StreamStart 后触发取消
        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, cts.Token))
        {
            events.Add(evt);
            if (events.Count == 2)
                cts.Cancel();
        }

        // Assert：迭代器正常终止且产出取消终态（修复前 OCE 直接逃逸）
        events.Should().Contain(e => e is LoopCancelledEvent);
        events[^1].Should().BeOfType<LoopCancelledEvent>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelledAfterEvents_ShouldDrainAlreadyProducedEvents()
    {
        // Arrange：运行器先推送一个 assistant delta 再阻塞，取消后该事件不应丢失
        var executor = CreateExecutor(new EmittingThenBlockingSessionRunner());
        var context = new AgentContext { SessionId = "sess-drain" };
        var messages = new List<ChatMessage> { new() { Role = "user", Content = "hi" } };
        var agent = new AgentDefinition
        {
            Name = "acp-opencode",
            Runtime = AgentRuntime.AcpPassthrough,
            AcpBackend = "opencode"
        };

        using var cts = new CancellationTokenSource();
        var events = new List<IMessageEvent>();

        // Act
        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, cts.Token))
        {
            events.Add(evt);
            if (events.Count == 2)
                cts.Cancel();
        }

        // Assert
        events.Should().Contain(e => e is LoopCancelledEvent);
        events.OfType<StreamDeltaEvent>().Should().NotBeEmpty();
    }

    private static AcpPassthroughExecutor CreateExecutor(IAcpSessionRunner runner)
    {
        var acpOptions = new AcpOptions
        {
            Enabled = true,
            DefaultBackend = "opencode",
            Backends = new Dictionary<string, AcpBackendConfig>
            {
                ["opencode"] = new() { Command = "opencode.cmd", Args = ["acp"] }
            }
        };
        var optionsMonitor = new Mock<IOptionsMonitor<AcpOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue).Returns(acpOptions);

        return new AcpPassthroughExecutor(
            runner,
            new ContentBlockMapper(),
            new AcpEventMapper(),
            optionsMonitor.Object,
            NullLogger<AcpPassthroughExecutor>.Instance);
    }

    private sealed class BlockingSessionRunner : IAcpSessionRunner
    {
        public async Task<AcpRunResult> RunAsync(
            AcpRunRequest request,
            IAcpUpdateSink sink,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new AcpRunResult { Success = true, Text = "" };
        }
    }

    private sealed class EmittingThenBlockingSessionRunner : IAcpSessionRunner
    {
        public async Task<AcpRunResult> RunAsync(
            AcpRunRequest request,
            IAcpUpdateSink sink,
            CancellationToken cancellationToken = default)
        {
            // EventYieldingSink 暴露直发通道，直接注入一个等价 delta 事件
            if (sink is EventYieldingSink yieldingSink)
            {
                await yieldingSink.PublishAsync(new StreamDeltaEvent
                {
                    SessionId = request.SeeingSessionId,
                    LoopId = request.LoopId,
                    ContentDelta = "partial"
                });
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new AcpRunResult { Success = true, Text = "partial" };
        }
    }
}
