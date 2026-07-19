using Seeing.Agent.App;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class IChatOrchestratorApiSurfaceTests
{
    [Fact]
    public void IChatOrchestrator_ShouldNotDeclare_ExecuteAsync_Or_Stop()
    {
        var methods = typeof(IChatOrchestrator).GetMethods();
        Assert.DoesNotContain(methods, m => m.Name == "ExecuteAsync");
        Assert.DoesNotContain(methods, m => m.Name == "Stop" && m.GetParameters().Length == 1);
    }
}
