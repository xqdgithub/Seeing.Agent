using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Scheduler.Tools;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerToolRegistrationTests
{
    [Fact]
    public void TryAddSingleton_ITool_IsSkippedWhenAlreadyRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITool>(new StubTool("read"));
        services.TryAddSingleton<ITool>(new StubTool("cron_list"));

        services.Where(d => d.ServiceType == typeof(ITool)).Should().HaveCount(1,
            "TryAddSingleton<ITool> must not be used after built-in tools are registered");
    }

    [Fact]
    public void AddSeeingSchedulerTools_AfterBuiltInITool_ShouldRegisterAllSixCronTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITool>(new StubTool("read"));
        services.AddSeeingSchedulerTools();

        services.Should().Contain(d => d.ServiceType == typeof(CronListTool));
        services.Should().Contain(d => d.ServiceType == typeof(CronCreateTool));
        services.Should().Contain(d => d.ServiceType == typeof(CronDeleteTool));
        services.Should().Contain(d => d.ServiceType == typeof(CronDisableTool));
        services.Should().Contain(d => d.ServiceType == typeof(CronResumeTool));
        services.Should().Contain(d => d.ServiceType == typeof(CronRunTool));

        // stub + 6 cron tools
        services.Where(d => d.ServiceType == typeof(ITool)).Should().HaveCount(7);
    }

    private sealed class StubTool : ITool
    {
        public StubTool(string id) => Id = id;
        public string Id { get; }
        public string Description => "stub";
        public JsonElement ParametersSchema => default;

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            throw new NotSupportedException();
    }
}
