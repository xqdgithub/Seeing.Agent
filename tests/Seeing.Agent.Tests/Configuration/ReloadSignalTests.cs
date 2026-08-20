using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadSignalTests
{
    [Fact]
    public void ConfigChange_默认空节列表()
    {
        var change = new ConfigChange();
        change.ChangedSections.Should().BeEmpty();
    }

    [Fact]
    public void ConfigChange_携带变更节()
    {
        var change = new ConfigChange { ChangedSections = new[] { "Providers" } };
        change.ChangedSections.Should().Contain("Providers");
    }

    [Fact]
    public void WorkspaceChange_携带新旧工作区()
    {
        var change = new WorkspaceChange { OldWorkspace = "/old", NewWorkspace = "/new" };
        change.OldWorkspace.Should().Be("/old");
        change.NewWorkspace.Should().Be("/new");
    }

    [Fact]
    public void 变更数据实现IReloadSignal接口()
    {
        new ConfigChange().Should().BeAssignableTo<IReloadSignal>();
        new WorkspaceChange().Should().BeAssignableTo<IReloadSignal>();
    }
}
