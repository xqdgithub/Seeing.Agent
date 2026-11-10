using FluentAssertions;
using Seeing.Agent.Abstractions.Ui.Workbench;

namespace Seeing.Agent.Workbench.Tests;

/// <summary>kind 命名与实例键是宿主与模块之间的字符串协议，必须双向可逆。</summary>
public class WorkbenchRequestKindsTests
{
    [Theory]
    [InlineData("files", "command")]
    [InlineData("markdown", "scroll")]
    [InlineData("video", "seek")]
    public void For_ThenTryParse_ShouldRoundTrip(string workbenchId, string action)
    {
        var kind = WorkbenchRequestKinds.For(workbenchId, action);

        WorkbenchRequestKinds.TryParse(kind, out var parsedId, out var parsedAction)
            .Should().BeTrue();

        parsedId.Should().Be(workbenchId);
        parsedAction.Should().Be(action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("workbench")]
    [InlineData("workbench.files")]
    [InlineData("workbench..command")]
    [InlineData("workbench.files.")]
    [InlineData("editor.command")]
    public void TryParse_InvalidKind_ShouldFail(string kind)
        => WorkbenchRequestKinds.TryParse(kind, out _, out _).Should().BeFalse();

    [Fact]
    public void TryParse_RegistryKind_ShouldFail()
    {
        // registry 是保留 id：注册表动作不属于任何工作台
        WorkbenchRequestKinds.TryParse(WorkbenchRequestKinds.RegistryOpen, out _, out _)
            .Should().BeFalse();
        WorkbenchRequestKinds.TryParse(WorkbenchRequestKinds.RegistryList, out _, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("workbench.registry", true)]
    [InlineData("workbench.registry.open", true)]
    [InlineData("workbench.registry.list", true)]
    [InlineData("workbench.files.command", false)]
    [InlineData("workbench", false)]
    public void IsRegistryKind_ShouldMatchPrefixOnly(string kind, bool expected)
        => WorkbenchRequestKinds.IsRegistryKind(kind).Should().Be(expected);

    [Fact]
    public void For_BlankArguments_ShouldThrow()
    {
        var act1 = () => WorkbenchRequestKinds.For("", "a");
        var act2 = () => WorkbenchRequestKinds.For("a", " ");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }
}

/// <summary>实例键的文本往返（localStorage 快照与诊断日志都依赖它）。</summary>
public class WorkbenchInstanceKeyTests
{
    [Theory]
    [InlineData(WorkbenchDock.Left, "files", 0)]
    [InlineData(WorkbenchDock.Right, "markdown", 0)]
    [InlineData(WorkbenchDock.Right, "files", 2)]
    public void ToString_ThenTryParse_ShouldRoundTrip(WorkbenchDock dock, string id, int ordinal)
    {
        var key = new WorkbenchInstanceKey(dock, id, ordinal);

        WorkbenchInstanceKey.TryParse(key.ToString(), out var parsed).Should().BeTrue();

        parsed.Should().Be(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("files")]
    [InlineData("Right:")]
    [InlineData("Nope:files#0")]
    [InlineData("Right:files#abc")]
    public void TryParse_InvalidText_ShouldFail(string? text)
        => WorkbenchInstanceKey.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void DifferentDocks_SameWorkbench_ShouldNotBeEqual()
    {
        var left = new WorkbenchInstanceKey(WorkbenchDock.Left, "files");
        var right = new WorkbenchInstanceKey(WorkbenchDock.Right, "files");

        left.Should().NotBe(right);
        left.ToString().Should().NotBe(right.ToString());
    }
}
