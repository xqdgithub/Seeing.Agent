using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Tests.Runtime;

public class RuntimeContractsTests
{
    [Fact]
    public void ISessionRuntime_ShouldDeclareDeliverInterruptStop()
    {
        var t = typeof(ISessionRuntime);
        t.GetMethod(nameof(ISessionRuntime.DeliverAsync)).Should().NotBeNull();
        t.GetMethod(nameof(ISessionRuntime.InterruptAsync)).Should().NotBeNull();
        t.GetMethod(nameof(ISessionRuntime.StopAsync)).Should().NotBeNull();
        t.GetMethod(nameof(ISessionRuntime.WaitForIdleAsync)).Should().NotBeNull();
    }

    [Fact]
    public void ISessionRuntime_ShouldNotDeclare_ExecutionLevelCancelOrGetExecution()
    {
        var names = typeof(ISessionRuntime).GetMethods().Select(m => m.Name).ToList();
        names.Should().NotContain("CancelAsync");
        names.Should().NotContain("GetExecution");
    }

    [Fact]
    public void ISessionDirectory_ShouldDeclareCrud()
    {
        var t = typeof(ISessionDirectory);
        foreach (var n in new[] { "GetAsync", "EnsureAsync", "ListAsync", "CreateAsync", "DeleteAsync", "RenameAsync", "BranchAsync" })
            t.GetMethod(n).Should().NotBeNull($"ISessionDirectory must declare {n}");
    }
}
