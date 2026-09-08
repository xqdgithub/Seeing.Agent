using System.Reflection;
using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Agents;

public class ExecutorContractsTests
{
    [Fact]
    public void IAgentExecutorImplementation_Should_Extend_IAgentExecutor()
    {
        typeof(IAgentExecutor).IsAssignableFrom(typeof(IAgentExecutorImplementation)).Should().BeTrue();
    }

    [Fact]
    public void IAgentExecutorImplementation_Should_Expose_SupportedRuntime()
    {
        var property = typeof(IAgentExecutorImplementation).GetProperty(nameof(IAgentExecutorImplementation.SupportedRuntime));

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(AgentRuntime));
        property.CanRead.Should().BeTrue();
        property.CanWrite.Should().BeFalse();
    }

    [Fact]
    public void IAgentExecutorImplementation_ExecuteAsync_Should_Match_IAgentExecutor_Signature()
    {
        var executorMethod = typeof(IAgentExecutor).GetMethod(nameof(IAgentExecutor.ExecuteAsync))!;
        var implementationMethod = typeof(IAgentExecutorImplementation).GetMethod(nameof(IAgentExecutorImplementation.ExecuteAsync))!;

        implementationMethod.Should().NotBeNull();
        implementationMethod!.ReturnType.Should().Be(executorMethod.ReturnType);
        implementationMethod.GetParameters().Select(p => (p.ParameterType, p.Name))
            .Should()
            .BeEquivalentTo(executorMethod.GetParameters().Select(p => (p.ParameterType, p.Name)));

        implementationMethod.ReturnType.Should().Be(typeof(IAsyncEnumerable<IMessageEvent>));
    }
}
