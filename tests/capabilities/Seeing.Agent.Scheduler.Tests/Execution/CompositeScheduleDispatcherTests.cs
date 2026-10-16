using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Execution;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Execution;

public class CompositeScheduleDispatcherTests
{
    [Fact]
    public async Task Dispatch_EnumeratesRegisteredDispatchersInRegistrationOrder_SkippingSelf()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("session", order));
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("log", order));
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("channel", order));
        services.AddSingleton<CompositeScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher, CompositeScheduleDispatcher>();

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IScheduledJobDispatcher>();
        dispatcher.Should().BeOfType<CompositeScheduleDispatcher>();

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "hello"
        });

        result.Success.Should().BeTrue();
        order.Should().Equal("session", "log", "channel");
    }

    [Fact]
    public async Task Dispatch_StopsOnFirstFailure()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("first", order));
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("fail", order, success: false));
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("third", order));
        services.AddSingleton<CompositeScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher, CompositeScheduleDispatcher>();

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IScheduledJobDispatcher>().DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "hello"
        });

        result.Success.Should().BeFalse();
        order.Should().Equal("first", "fail");
    }

    private sealed class NamedDispatcher(string name, List<string> order, bool success = true) : IScheduledJobDispatcher
    {
        public Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
        {
            order.Add(name);
            return Task.FromResult(success ? DispatchResult.Ok() : DispatchResult.Fail(name));
        }
    }
}
