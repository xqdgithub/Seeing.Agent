using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Desktop.Tests;

/// <summary>
/// 桌面启动顺序约束：启动决议必须用「用户真正启动时」的目录。
/// <para>
/// <c>DesktopWorkspace.Apply()</c> 会把进程当前目录改到可写位置（双击时通常是
/// <c>%LOCALAPPDATA%\Seeing</c>）。如果 WebUI 宿主在 <c>Apply()</c> 之后才读当前目录，
/// 决议看到的就是那个可写位置而不是用户的启动目录，历史匹配会失效。
/// </para>
/// </summary>
public class DesktopLaunchOrderTests
{
    [Fact]
    public void Program_Should_CaptureStartupDirectory_Before_Apply()
    {
        var source = ReadProgram();

        var captureIndex = source.IndexOf("captureStartupDirectory", StringComparison.Ordinal);
        var applyIndex = source.IndexOf("DesktopWorkspace.Apply()", StringComparison.Ordinal);

        captureIndex.Should().BeGreaterThan(-1, "桌面必须先捕获原始启动目录");
        applyIndex.Should().BeGreaterThan(-1, "桌面仍要摆正起始位置");
        captureIndex.Should().BeLessThan(applyIndex, "捕获必须在改写当前目录之前");
    }

    [Fact]
    public void Program_Should_PassCapturedDirectory_To_WebUiHost()
    {
        var source = ReadProgram();

        source.Should().Contain("captureStartupDirectory)",
            "捕获的目录必须作为 BuildAsync 的最后一个参数传给宿主");
    }

    private static string ReadProgram()
    {
        var repoRoot = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(repoRoot, "samples", "Seeing.Agent.Desktop", "Program.cs"));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根（Seeing.Agent.slnx）");
    }
}
