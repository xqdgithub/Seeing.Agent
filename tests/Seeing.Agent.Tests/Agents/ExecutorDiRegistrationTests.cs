using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core;
using Seeing.Agent.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Agents;

public class ExecutorDiRegistrationTests
{
    [Fact]
    public void AddSeeingAgent_Registers_Exactly_One_IAgentExecutor_As_Router()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeeingAgent();

        var executorDescriptors = services.Where(d => d.ServiceType == typeof(IAgentExecutor)).ToList();
        executorDescriptors.Should().HaveCount(1);
        executorDescriptors[0].ImplementationType.Should().Be(typeof(AgentExecutorRouter));
    }

    [Fact]
    public void AddSeeingAgent_Registers_Native_As_IAgentExecutorImplementation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeeingAgent();

        var implementations = services
            .Where(d => d.ServiceType == typeof(IAgentExecutorImplementation))
            .ToList();

        implementations.Should().HaveCount(1);
        implementations[0].ImplementationType.Should().Be(typeof(NativeAgentExecutor));
    }

    [Fact]
    public void NativeAgentExecutor_SupportedRuntime_Is_Native()
    {
        typeof(IAgentExecutorImplementation).IsAssignableFrom(typeof(NativeAgentExecutor)).Should().BeTrue();

        IAgentExecutorImplementation instance = new NativeAgentExecutor(null!);
        instance.SupportedRuntime.Should().Be(AgentRuntime.Native);
    }
}
