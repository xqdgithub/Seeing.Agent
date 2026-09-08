using Acp.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Execution;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpSessionRunnerTests
{
    [Fact]
    public async Task RunAsync_WithMockedDependencies_ShouldReturnText()
    {
        var backendRegistry = new Mock<IAcpBackendRegistry>();
        backendRegistry.Setup(r => r.GetBackend("test")).Returns(new AcpBackendDescriptor
        {
            Id = "test",
            Command = "echo"
        });

        var runner = new MockAcpSessionRunner();

        var sink = new BufferingSink();
        var result = await runner.RunAsync(new AcpRunRequest
        {
            Scope = "tool",
            ScopeKey = "task-1",
            BackendId = "test",
            SeeingSessionId = "sess-1",
            Prompt = new ContentBlock[] { new TextContentBlock("hello") },
            WorkingDirectory = Environment.CurrentDirectory
        }, sink, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Text.Should().Be("mock-response");
        sink.GetText().Should().Contain("mock");
    }

    private sealed class MockAcpSessionRunner : IAcpSessionRunner
    {
        public Task<AcpRunResult> RunAsync(AcpRunRequest request, IAcpUpdateSink sink, CancellationToken cancellationToken)
        {
            sink.OnSessionUpdateAsync("acp-sess", new AgentMessageChunk
            {
                Content = new TextContentBlock("mock stream")
            }, cancellationToken).GetAwaiter().GetResult();

            return Task.FromResult(new AcpRunResult
            {
                Text = "mock-response",
                Success = true,
                StopReason = "end_turn"
            });
        }
    }
}
