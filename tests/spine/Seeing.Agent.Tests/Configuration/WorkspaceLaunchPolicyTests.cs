using FluentAssertions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class WorkspaceLaunchPolicyTests
{
    private const string Exe = @"C:\Program Files\Seeing";

    private static WorkspaceLaunchInput Input(
        WorkspaceLaunchMode mode,
        string startup,
        IReadOnlyList<string>? recent = null,
        string? lastInteractive = null,
        string? explicitPath = null,
        string? inherited = null,
        string? exe = Exe)
        => new(mode, startup, exe, explicitPath, inherited,
            new WorkspaceHistorySnapshot(recent ?? Array.Empty<string>(), lastInteractive));

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return p => set.Contains(Path.TrimEndingDirectorySeparator(p));
    }

    [Fact]
    public void Invocation_ShouldUseStartupDirectory_AndIgnoreInheritedEnvironment()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Invocation, @"D:\tool\a", inherited: @"E:\stale"),
            Existing(@"D:\tool\a", @"E:\stale"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Resolved);
        decision.WorkspaceRoot.Should().Be(@"D:\tool\a");
        decision.Source.Should().Be(WorkspaceResolutionSource.StartupDirectory);
        decision.UpdateRecent.Should().BeTrue();
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Invocation_TuiExplicitPath_ShouldWinOverStartupDirectory()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Invocation, @"D:\tool\a", explicitPath: @"D:\tool\b"),
            Existing(@"D:\tool\a", @"D:\tool\b"));

        decision.WorkspaceRoot.Should().Be(@"D:\tool\b");
    }

    [Fact]
    public void Invocation_MissingStartupDirectory_ShouldFail()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Invocation, @"D:\gone"), Existing());

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
        decision.Error.Should().Contain(@"D:\gone");
    }

    [Fact]
    public void Interactive_ExplicitPath_ShouldBeatEnvironmentAndHistory()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd",
                recent: [@"D:\hist"], lastInteractive: @"D:\hist",
                explicitPath: @"D:\explicit", inherited: @"D:\env"),
            Existing(@"D:\cwd", @"D:\hist", @"D:\explicit", @"D:\env"));

        decision.WorkspaceRoot.Should().Be(@"D:\explicit");
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Interactive_MissingExplicitPath_ShouldFailWithoutFallingBack()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd", lastInteractive: @"D:\cwd",
                explicitPath: @"D:\gone"),
            Existing(@"D:\cwd"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
        decision.WorkspaceRoot.Should().BeNull();
    }

    [Fact]
    public void Interactive_MissingInheritedEnvironment_ShouldFail()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd", lastInteractive: @"D:\cwd",
                inherited: @"D:\gone"),
            Existing(@"D:\cwd"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
    }

    [Fact]
    public void Interactive_StartupDirectoryInHistory_ShouldWin_WithoutTouchingLastInteractive()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd",
                recent: [@"D:\other", @"D:\cwd"], lastInteractive: @"D:\other"),
            Existing(@"D:\cwd", @"D:\other"));

        decision.WorkspaceRoot.Should().Be(@"D:\cwd");
        decision.UpdateRecent.Should().BeFalse("已经在最近列表里，不必重写");
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Interactive_StartupDirectoryEqualsExe_ShouldSkipHistoryHit()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, Exe, recent: [Exe], lastInteractive: @"D:\last"),
            Existing(Exe, @"D:\last"));

        decision.WorkspaceRoot.Should().Be(@"D:\last");
    }

    [Fact]
    public void Interactive_FallsBackToLastInteractive()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\unrelated",
                recent: [@"D:\hist"], lastInteractive: @"D:\hist"),
            Existing(@"D:\unrelated", @"D:\hist"));

        decision.WorkspaceRoot.Should().Be(@"D:\hist");
        decision.UpdateRecent.Should().BeFalse();
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Interactive_MissingLastInteractive_ShouldBeUnresolved()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\unrelated",
                recent: [@"D:\gone"], lastInteractive: @"D:\gone"),
            Existing(@"D:\unrelated"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Unresolved);
        decision.WorkspaceRoot.Should().BeNull();
    }

    [Fact]
    public void Service_ShouldFallBackToStartupDirectory_AndOnlyUpdateRecent()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Service, @"E:\svc", recent: [@"D:\hist"], lastInteractive: @"D:\hist"),
            Existing(@"E:\svc", @"D:\hist"));

        decision.WorkspaceRoot.Should().Be(@"E:\svc");
        decision.UpdateRecent.Should().BeTrue();
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Service_MissingStartupDirectory_ShouldFail()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Service, @"E:\gone", lastInteractive: @"D:\hist"),
            Existing(@"D:\hist"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
    }
}
