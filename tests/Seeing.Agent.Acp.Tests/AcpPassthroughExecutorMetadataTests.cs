using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpPassthroughExecutorMetadataTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldPassModelAndModeFromContextMetadataToSessionRunner()
    {
        AcpRunRequest? captured = null;
        var sessionRunner = new CapturingSessionRunner(req =>
        {
            captured = req;
            return Task.FromResult(new AcpRunResult { Success = true, Text = "ok" });
        });

        var executor = new AcpPassthroughExecutor(
            sessionRunner,
            new ContentBlockMapper(),
            new AcpEventMapper(),
            Options.Create(new SeeingAgentOptions
            {
                Acp = new AcpOptions
                {
                    Enabled = true,
                    DefaultBackend = "opencode",
                    Backends = new Dictionary<string, AcpBackendConfig>
                    {
                        ["opencode"] = new() { Command = "opencode.cmd", Args = ["acp"] }
                    }
                }
            }),
            NullLogger<AcpPassthroughExecutor>.Instance);

        var context = new AgentContext
        {
            SessionId = "sess-1",
            History = [new ChatMessage { Role = "user", Content = "hello" }]
        };
        context.Metadata[AgentContextKeys.RequestModelId] = "seeing-coding-plan/GLM-5";
        context.Metadata[AgentContextKeys.AcpModeId] = "build";

        var agent = new AgentDefinition
        {
            Name = "acp-opencode",
            Runtime = AgentRuntime.AcpPassthrough,
            AcpBackend = "opencode"
        };

        await foreach (var _ in executor.ExecuteAsync(agent, context))
        {
        }

        captured.Should().NotBeNull();
        captured!.ModelId.Should().Be("seeing-coding-plan/GLM-5");
        captured.ModeId.Should().Be("build");
    }

    private sealed class CapturingSessionRunner : IAcpSessionRunner
    {
        private readonly Func<AcpRunRequest, Task<AcpRunResult>> _handler;

        public CapturingSessionRunner(Func<AcpRunRequest, Task<AcpRunResult>> handler) =>
            _handler = handler;

        public Task<AcpRunResult> RunAsync(
            AcpRunRequest request,
            IAcpUpdateSink sink,
            CancellationToken cancellationToken = default) =>
            _handler(request);
    }
}
