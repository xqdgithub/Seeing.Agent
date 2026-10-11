using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// 旧 IChatOrchestrator 已拆分为 ISessionRuntime（执行）+ ISessionDirectory（CRUD）；
/// 本测试锁定两接口职责边界：执行入口不得混入 CRUD，CRUD 目录不得混入执行。
/// </summary>
public class SessionApiSurfaceTests
{
    [Fact]
    public void ISessionRuntime_ShouldNotDeclare_CrudNorLegacyExecute()
    {
        var methods = typeof(ISessionRuntime).GetMethods();
        Assert.DoesNotContain(methods, m => m.Name == "ExecuteAsync");
        Assert.DoesNotContain(methods, m => m.Name == "Stop" && m.GetParameters().Length == 1);
        Assert.DoesNotContain(methods, m => m.Name == "CreateSessionAsync");
        Assert.DoesNotContain(methods, m => m.Name == "DeleteSessionAsync");
        Assert.DoesNotContain(methods, m => m.Name == "RenameSessionAsync");
        Assert.DoesNotContain(methods, m => m.Name == "BranchSessionAsync");
        Assert.DoesNotContain(methods, m => m.Name == "ListSessionsAsync");
    }

    [Fact]
    public void ISessionDirectory_ShouldNotDeclare_ExecutionEntry()
    {
        var methods = typeof(ISessionDirectory).GetMethods();
        Assert.DoesNotContain(methods, m => m.Name == "DeliverAsync");
        Assert.DoesNotContain(methods, m => m.Name == "InterruptAsync");
        Assert.DoesNotContain(methods, m => m.Name == "StopAsync");
    }
}
