using FluentAssertions;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

/// <summary>
/// SessionWindow Summary 紧凑卡（已完成 / 子代理）必须保留来源行（设计 §4.7）。
/// </summary>
public class SessionWindowCompactSourceTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("无法定位仓库根目录（未找到 Seeing.Agent.slnx）");
    }

    private static string SessionWindowPath => Path.Combine(
        RepoRoot, "samples", "Seeing.Agent.WebUI", "Components", "SessionWindow.razor");

    [Fact]
    public void CompactSummary_ShouldRenderSourceLine()
    {
        var source = File.ReadAllText(SessionWindowPath);

        var start = source.IndexOf("conference-tile-compact\">", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "应存在紧凑卡分支");

        var end = source.IndexOf("conference-tile-compact-preview", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "紧凑卡应含预览摘要");

        var compactBlock = source[start..end];
        compactBlock.Should().Contain("GetSourceLine()", "紧凑卡必须保留来源行（§4.7）");
    }
}
