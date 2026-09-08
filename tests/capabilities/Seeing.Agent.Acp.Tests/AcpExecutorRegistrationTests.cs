using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpExecutorRegistrationTests
{
    [Fact]
    public void AddSeeingAcp_Does_Not_Expose_ReplaceExecutionRouter()
    {
        var method = typeof(AcpServiceCollectionExtensions).GetMethod(
            "ReplaceExecutionRouter",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        method.Should().BeNull("ReplaceExecutionRouter must be deleted; Router aggregates implementations instead");
    }

    [Fact]
    public void AddSeeingCore_Plus_Acp_Registers_One_IAgentExecutor_And_Multiple_Implementations()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingAcp(registry);
        services.AddSeeingCore(registry);

        var executors = services.Where(d => d.ServiceType == typeof(IAgentExecutor)).ToList();
        executors.Should().HaveCount(1);
        executors[0].ImplementationType.Should().Be(typeof(AgentExecutorRouter));

        var implementations = services
            .Where(d => d.ServiceType == typeof(IAgentExecutorImplementation))
            .Select(d => d.ImplementationType)
            .ToList();

        implementations.Should().HaveCount(2);
        implementations.Should().Contain(typeof(NativeAgentExecutor));
        implementations.Should().Contain(typeof(AcpAgentExecutor));

        // No Replace pattern: IAgentExecutor must not be remapped to AcpAgentExecutor
        executors.Should().NotContain(d => d.ImplementationType == typeof(AcpAgentExecutor));
    }

    [Fact]
    public void AcpAgentExecutor_SupportedRuntime_Is_AcpPassthrough_And_Has_No_Inner_Executor_Field()
    {
        typeof(IAgentExecutorImplementation).IsAssignableFrom(typeof(AcpAgentExecutor)).Should().BeTrue();

        var fields = typeof(AcpAgentExecutor).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        fields.Should().NotContain(f => f.Name.Contains("inner", StringComparison.OrdinalIgnoreCase)
            || f.FieldType == typeof(IAgentExecutor)
            || f.FieldType == typeof(NativeAgentExecutor));

        var ctors = typeof(AcpAgentExecutor).GetConstructors();
        ctors.Should().ContainSingle();
        ctors[0].GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(NativeAgentExecutor))
            .And.NotContain(typeof(IAgentExecutor));

        IAgentExecutorImplementation instance = new AcpAgentExecutor(null!, null!);
        instance.SupportedRuntime.Should().Be(AgentRuntime.AcpPassthrough);
    }
}
