using System;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace Seeing.Session.Tests.Execution
{
    public class IExecutionStateTests
    {
        [Fact]
        public void IExecutionState_Should_Define_All_Members_As_Specified()
        {
            var t = typeof(Seeing.Session.Execution.IExecutionState);

            // Properties
            t.GetProperty("IsExecuting").Should().NotBeNull("IsExecuting property must exist");
            t.GetProperty("IsPaused").Should().NotBeNull("IsPaused property must exist");
            t.GetProperty("LastError").Should().NotBeNull("LastError property must exist");

            // Methods
            t.GetMethod("StartExecutionAsync", Type.EmptyTypes).Should().NotBeNull("StartExecutionAsync() signature must exist");
            t.GetMethod("PauseExecutionAsync", Type.EmptyTypes).Should().NotBeNull("PauseExecutionAsync() signature must exist");
            t.GetMethod("ResumeExecutionAsync", Type.EmptyTypes).Should().NotBeNull("ResumeExecutionAsync() signature must exist");
            t.GetMethod("CancelExecutionAsync", Type.EmptyTypes).Should().NotBeNull("CancelExecutionAsync() signature must exist");

            // Cancellation token
            var ctMethod = t.GetMethod("GetCancellationToken", Type.EmptyTypes);
            ctMethod.Should().NotBeNull("GetCancellationToken() signature must exist");
            ctMethod.ReturnType.Should().Be(typeof(CancellationToken), "GetCancellationToken() should return CancellationToken");
        }
    }
}
