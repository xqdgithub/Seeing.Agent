using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class ExecutionAbstractionsContractsTests
{
    [Fact]
    public void ISessionRuntime_ShouldDeclareDeliverInterruptStopAndOverview()
    {
        var type = typeof(ISessionRuntime);

        var deliver = type.GetMethod(nameof(ISessionRuntime.DeliverAsync));
        deliver.Should().NotBeNull();
        deliver!.ReturnType.Should().Be(typeof(Task<InboxResult>));
        deliver.GetParameters()[0].ParameterType.Should().Be(typeof(InboxMessage));

        type.GetMethod(nameof(ISessionRuntime.InterruptAsync), [typeof(string), typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task<bool>));
        type.GetMethod(nameof(ISessionRuntime.StopAsync), [typeof(string), typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task<int>));
        type.GetMethod(nameof(ISessionRuntime.CancelAllInFlightAsync), [typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task<int>));
        type.GetMethod(nameof(ISessionRuntime.WaitForIdleAsync), [typeof(string), typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task));
        type.GetMethod(nameof(ISessionRuntime.GetOverview), [typeof(string)])
            !.ReturnType.Should().Be(typeof(SessionExecutionOverview));
        type.GetMethod(nameof(ISessionRuntime.HasAnyActiveExecution), Type.EmptyTypes)
            !.ReturnType.Should().Be(typeof(bool));
        type.GetMethod(nameof(ISessionRuntime.ReconcileIncompleteTasksAsync), [typeof(string), typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task<int>));
    }

    [Fact]
    public void IExecutionInFlightBoundary_ShouldDeclareProbeAndCancel()
    {
        var type = typeof(IExecutionInFlightBoundary);
        type.GetMethod(nameof(IExecutionInFlightBoundary.HasInFlight), Type.EmptyTypes)
            !.ReturnType.Should().Be(typeof(bool));
        type.GetMethod(nameof(IExecutionInFlightBoundary.ListInFlightExecutionIds), Type.EmptyTypes)
            .Should().NotBeNull();
        type.GetMethod(nameof(IExecutionInFlightBoundary.CancelAllInFlightAsync), [typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task<int>));
    }

    [Fact]
    public void ExecutionSubmitResult_ShouldExposeStatusAndFactoryMethods()
    {
        var type = typeof(ExecutionSubmitResult);

        type.GetProperty(nameof(ExecutionSubmitResult.Success))!.PropertyType.Should().Be(typeof(bool));
        type.GetProperty(nameof(ExecutionSubmitResult.Status))!.PropertyType.Should().Be(typeof(ExecutionStatus));

        type.GetMethod(nameof(ExecutionSubmitResult.Succeeded), [typeof(string)]).Should().NotBeNull();
        type.GetMethod(nameof(ExecutionSubmitResult.Queued), [typeof(string), typeof(int)]).Should().NotBeNull();
        type.GetMethod(nameof(ExecutionSubmitResult.Failed), [typeof(string)]).Should().NotBeNull();
    }

    [Fact]
    public void ExecutionRecord_ShouldReferenceChatInputAndOptions()
    {
        var type = typeof(ExecutionRecord);

        type.GetProperty(nameof(ExecutionRecord.Input))!.PropertyType.Should().Be(typeof(ChatInput));
        type.GetProperty(nameof(ExecutionRecord.Options))!.PropertyType.Should().Be(typeof(ChatOptions));
        type.GetProperty(nameof(ExecutionRecord.Status))!.PropertyType.Should().Be(typeof(ExecutionStatus));
    }
}
