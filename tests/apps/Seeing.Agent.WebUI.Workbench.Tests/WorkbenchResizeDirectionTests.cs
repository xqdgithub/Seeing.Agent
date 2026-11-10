using FluentAssertions;
using System.Text.RegularExpressions;

namespace Seeing.Agent.WebUI.Workbench.Tests;

/// <summary>
/// 守门：分隔线拖动方向必须与 dock 所在侧一致。
/// <para>
/// <b>为什么这条需要单独守</b>：分隔线永远贴在「dock 与主列之间」那条边上 ——
/// 右侧 dock 的分隔线在<b>左缘</b>，左侧 dock 的在<b>右缘</b>。所以「向右拖」对两者的
/// 语义正好相反：右侧应变窄、左侧应变宽。写反了不会报错，只表现为「越拖越反」，
/// 只能在浏览器里肉眼发现。
/// </para>
/// <para>
/// 约定：<c>Invert = !IsLeft</c>。历史实现（旧文件面板）是同一条规则。
/// </para>
/// </summary>
public class WorkbenchResizeDirectionTests
{
    /// <summary>右侧 dock：分隔线在左缘，Invert 必须为 true。</summary>
    [Fact]
    public void RightDock_ResizeHandle_ShouldInvert()
    {
        var right = ReadDockHostSection(isLeftBranch: false);

        right.Should().Contain("Invert=\"@(!IsLeft)\"",
            "右侧 dock 的分隔线在左缘：向右拖必须收窄（invert）");
        right.Should().NotContain("Invert=\"false\"",
            "Invert=\"false\" 会让右侧 dock 越向右拖越宽（方向反了）");
    }

    /// <summary>左侧 dock：分隔线在右缘，Invert 必须为 false。</summary>
    [Fact]
    public void LeftDock_ResizeHandle_ShouldNotInvert()
    {
        var left = ReadDockHostSection(isLeftBranch: true);

        left.Should().Contain("Invert=\"@(!IsLeft)\"",
            "左侧 dock 的分隔线在右缘：向右拖必须变宽（不 invert）");
        left.Should().NotContain("Invert=\"true\"",
            "Invert=\"true\" 会让左侧 dock 越向右拖越窄（方向反了）");
    }

    /// <summary>分隔线的 DOM 顺序必须与「贴主列那一侧」一致。</summary>
    [Fact]
    public void ResizeHandle_ShouldSitOnTheMainColumnSide()
    {
        var content = ReadDockHostFile();

        var rightHandle = content.IndexOf("Invert=\"@(!IsLeft)\"", StringComparison.Ordinal);
        var panelOpen = content.IndexOf("<div class=\"workbench-panel\">", StringComparison.Ordinal);

        rightHandle.Should().BeGreaterThan(-1);
        panelOpen.Should().BeGreaterThan(-1);

        // 右侧 dock 的分隔线写在 panel 之前（贴左缘）
        rightHandle.Should().BeLessThan(panelOpen,
            "右侧 dock 的分隔线必须渲染在面板之前（左缘），否则拖动手柄会跑到贴屏那边");
    }

    /// <summary>JS 侧：invert 为真时 delta 取反。</summary>
    [Fact]
    public void JavaScript_ShouldNegateDeltaWhenInverted()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "samples", "Seeing.Agent.WebUI.Workbench", "wwwroot", "workbench.js");
        File.Exists(path).Should().BeTrue($"应找到 {path}");

        var js = File.ReadAllText(path);

        js.Should().Contain("(invert ? -delta : delta)",
            "JS 必须按 invert 取反拖动增量；两侧共用同一段逻辑");
    }

    // ── helpers ──

    private static string ReadDockHostFile()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root, "samples", "Seeing.Agent.WebUI.Workbench", "Components", "WorkbenchDockHost.razor");

        File.Exists(path).Should().BeTrue($"应找到 {path}");
        return File.ReadAllText(path);
    }

    /// <summary>取 <c>@if (!IsLeft)</c> 或 <c>@if (IsLeft)</c> 分支体（到下一个 <c>}</c> 收尾）。</summary>
    private static string ReadDockHostSection(bool isLeftBranch)
    {
        var content = ReadDockHostFile();
        var marker = isLeftBranch ? "@if (IsLeft)" : "@if (!IsLeft)";

        var start = content.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"WorkbenchDockHost 应包含 '{marker}' 分支");

        var end = content.IndexOf("</div>", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        return content[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(CurrentFile())!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
