using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class AgentExecutorRouterTests
{
    [Fact]
    public async Task ExecuteAsync_Dispatches_To_Implementation_Matching_Runtime()
    {
        var nativeCalled = false;
        var acpCalled = false;

        var native = CreateImplementation(AgentRuntime.Native, () =>
        {
            nativeCalled = true;
            return AsyncEvents();
        });
        var acp = CreateImplementation(AgentRuntime.AcpPassthrough, () =>
        {
            acpCalled = true;
            return AsyncEvents();
        });

        var router = new AgentExecutorRouter(new[] { native.Object, acp.Object });
        var definition = new AgentDefinition { Name = "n", Runtime = AgentRuntime.Native };
        var context = new AgentContext { SessionId = "s1" };

        await foreach (var _ in router.ExecuteAsync(definition, Array.Empty<ChatMessage>(), context))
        {
        }

        nativeCalled.Should().BeTrue();
        acpCalled.Should().BeFalse();
        native.Verify(
            x => x.ExecuteAsync(definition, It.IsAny<IReadOnlyList<ChatMessage>>(), context, It.IsAny<CancellationToken>()),
            Times.Once);
        acp.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentDefinition>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Dispatches_AcpPassthrough_To_Acp_Implementation()
    {
        var native = CreateImplementation(AgentRuntime.Native, AsyncEvents);
        var acp = CreateImplementation(AgentRuntime.AcpPassthrough, AsyncEvents);

        var router = new AgentExecutorRouter(new[] { native.Object, acp.Object });
        var definition = new AgentDefinition { Name = "a", Runtime = AgentRuntime.AcpPassthrough };
        var context = new AgentContext { SessionId = "s1" };

        await foreach (var _ in router.ExecuteAsync(definition, Array.Empty<ChatMessage>(), context))
        {
        }

        acp.Verify(
            x => x.ExecuteAsync(definition, It.IsAny<IReadOnlyList<ChatMessage>>(), context, It.IsAny<CancellationToken>()),
            Times.Once);
        native.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentDefinition>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Throws_When_No_Implementation_For_Runtime()
    {
        var native = CreateImplementation(AgentRuntime.Native, AsyncEvents);
        var router = new AgentExecutorRouter(new[] { native.Object });
        var definition = new AgentDefinition { Name = "a", Runtime = AgentRuntime.AcpPassthrough };

        var act = async () =>
        {
            await foreach (var _ in router.ExecuteAsync(definition, Array.Empty<ChatMessage>(), new AgentContext { SessionId = "s1" }))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AcpPassthrough*");
    }

    private static Mock<IAgentExecutorImplementation> CreateImplementation(
        AgentRuntime runtime,
        Func<IAsyncEnumerable<IMessageEvent>> execute)
    {
        var mock = new Mock<IAgentExecutorImplementation>();
        mock.SetupGet(x => x.SupportedRuntime).Returns(runtime);
        mock.Setup(x => x.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken _) => execute());
        return mock;
    }

    private static async IAsyncEnumerable<IMessageEvent> AsyncEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}
