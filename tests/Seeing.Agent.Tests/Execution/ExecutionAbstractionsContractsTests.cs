using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class ExecutionAbstractionsContractsTests
{
    [Fact]
    public void IExecutionStatusProvider_ShouldDeclareOverviewAndGetExecution()
    {
        var type = typeof(IExecutionStatusProvider);

        type.GetMethod(nameof(IExecutionStatusProvider.GetOverview), [typeof(string)])
            !.ReturnType.Should().Be(typeof(SessionExecutionOverview));
        type.GetMethod(nameof(IExecutionStatusProvider.GetExecution), [typeof(string)])
            !.ReturnType.Should().Be(typeof(ExecutionRecord));
        type.GetMethod(nameof(IExecutionStatusProvider.HasAnyActiveExecution), Type.EmptyTypes)
            !.ReturnType.Should().Be(typeof(bool));
    }

    [Fact]
    public void IExecutionSubmitter_ShouldDeclareSubmitCancelAndWait()
    {
        var type = typeof(IExecutionSubmitter);

        var submit = type.GetMethod(nameof(IExecutionSubmitter.SubmitAsync));
        submit.Should().NotBeNull();
        submit!.ReturnType.Should().Be(typeof(Task<ExecutionSubmitResult>));
        submit.GetParameters().Should().HaveCount(4);
        submit.GetParameters()[0].ParameterType.Should().Be(typeof(string));
        submit.GetParameters()[1].ParameterType.Should().Be(typeof(ChatInput));
        submit.GetParameters()[2].ParameterType.Should().Be(typeof(ChatOptions));
        submit.GetParameters()[3].ParameterType.Should().Be(typeof(CancellationToken));

        type.GetMethod(nameof(IExecutionSubmitter.CancelAsync), [typeof(string), typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task<bool>));
        type.GetMethod(nameof(IExecutionSubmitter.CancelBySessionAsync), [typeof(string), typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task<int>));
        type.GetMethod(nameof(IExecutionSubmitter.WaitForExecutionAsync), [typeof(string), typeof(CancellationToken)])
            !.ReturnType.Should().Be(typeof(Task));
        type.GetMethod(nameof(IExecutionSubmitter.CancelAllInFlightAsync), [typeof(CancellationToken)])
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
