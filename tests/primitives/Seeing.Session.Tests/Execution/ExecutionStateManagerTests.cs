using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Seeing.Session.Execution;
using Xunit;

namespace Seeing.Session.Tests.Execution
{
    public class ExecutionStateManagerTests
    {
        [Fact]
        public async Task StartExecutionAsync_Should_Set_IsExecuting_And_Create_CancellationToken()
        {
            var state = new ExecutionStateManager();
            await state.StartExecutionAsync();

            state.IsExecuting.Should().BeTrue("StartExecutionAsync should set IsExecuting to true");
            state.IsPaused.Should().BeFalse("New execution should not be paused");
            var token = state.GetCancellationToken();
            token.Should().NotBeNull();
            token.CanBeCanceled.Should().BeTrue();
        }

        [Fact]
        public async Task PauseAndResume_Should_Toggle_Paused_State()
        {
            var state = new ExecutionStateManager();
            await state.StartExecutionAsync();

            await state.PauseExecutionAsync();
            state.IsPaused.Should().BeTrue("Execution should be paused after PauseExecutionAsync");

            await state.ResumeExecutionAsync();
            state.IsPaused.Should().BeFalse("Execution should resume after ResumeExecutionAsync");
        }

        [Fact]
        public async Task CancelExecutionAsync_Should_Cancel_And_Reset_State()
        {
            var state = new ExecutionStateManager();
            await state.StartExecutionAsync();

            var token = state.GetCancellationToken();
            await state.CancelExecutionAsync();

            state.IsExecuting.Should().BeFalse("Cancellation should stop execution");
            state.IsPaused.Should().BeFalse("Cancellation should clear paused state");
            token.IsCancellationRequested.Should().BeTrue("Cancellation token should be canceled after CancelExecutionAsync");
        }

        [Fact]
        public async Task StartExecution_After_Cancel_Should_Create_New_Token()
        {
            var state = new ExecutionStateManager();
            await state.StartExecutionAsync();
            var firstToken = state.GetCancellationToken();
            await state.CancelExecutionAsync();

            await state.StartExecutionAsync();
            var secondToken = state.GetCancellationToken();

            firstToken.Should().NotBeNull();
            secondToken.Should().NotBeNull();
            // After restart, tokens should be different (different CTS)
            firstToken.Equals(secondToken).Should().BeFalse("Token from new CTS should differ from the previous token");
        }
    }
}
