using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

/// <summary>
/// 验证 WebUI 静态资源管道的一致性，防止 JS 互操作因脚本 404 而失败。
/// 这些测试聚焦于 MainLayout.razor 抛出的 "isMobileBrowser is not a function" 错误真正根因：
/// Program.cs 未启用 .NET 9+ StaticWebAssets 管道，导致 wwwroot 资源全部 404。
/// </summary>
public class WebUiScriptInteropContractTests
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

    private static string WebUiPath => Path.Combine(RepoRoot, "samples", "Seeing.Agent.WebUI");

    private static string HostCshtmlPath => Path.Combine(WebUiPath, "Pages", "_Host.cshtml");

    private static string AppJsPath => Path.Combine(WebUiPath, "wwwroot", "js", "app.js");

    private static string ProgramPath => Path.Combine(WebUiPath, "Program.cs");

    [Fact]
    public void HostPage_Should_Load_AppJs_Before_BlazorServerStarts()
    {
        var host = File.ReadAllText(HostCshtmlPath);
        host.Should().Contain("<script src=\"js/app.js\"",
            "宿主页 _Host.cshtml 必须加载 js/app.js，否则 13 处 JS 互操作将全部失败");
    }

    [Fact]
    public void AppJs_Should_Define_IsMobileBrowser()
    {
        var js = File.ReadAllText(AppJsPath);
        var pattern = new Regex(@"\bfunction\s+isMobileBrowser\s*\(");
        pattern.IsMatch(js).Should().BeTrue(
            "app.js 必须定义 isMobileBrowser 函数，否则 MainLayout.razor:81 调用会失败");
    }

    [Fact]
    public void AppJs_Should_Define_Every_JsInterop_Function_Referenced_In_Razor()
    {
        var js = File.ReadAllText(AppJsPath);

        var razorFiles = Directory
            .EnumerateFiles(WebUiPath, "*.razor", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        var invokePattern = new Regex(
            @"JSRuntime\.(?:InvokeAsync|InvokeVoidAsync)[^""]*""([^""]+)""",
            RegexOptions.Compiled);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var razor in razorFiles)
        {
            var content = File.ReadAllText(razor);
            foreach (Match m in invokePattern.Matches(content))
            {
                var name = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Contains('.')) continue;

                var defPattern = new Regex(@"\b(?:function\s+)?" + Regex.Escape(name) + @"\s*\(");
                if (!defPattern.IsMatch(js))
                {
                    missing.Add($"{Path.GetFileName(razor)} -> {name}");
                }
            }
        }

        missing.Should().BeEmpty(
            "以下 JS 函数在 app.js 中未定义，将导致 'X is not a function' 错误:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void Program_Should_Enable_StaticWebAssets_Pipeline()
    {
        // 真正根因：.NET 9+ StaticWebAssets 模式下，bin\<Config>\<TFM>\wwwroot 不再被复制。
        // Program.cs 仅调用 app.UseStaticFiles() 时，中间件会查找不存在的 wwwroot 目录，
        // 导致 js/app.js、css/*.css、_framework/blazor.server.js 全部返回 404。
        // 浏览器拿到 404 后，window.isMobileBrowser 未定义，
        // 客户端 JSRuntime.InvokeAsync("isMobileBrowser") 抛 'is not a function'。
        // 必须显式启用：app.UseStaticWebAssets()（.NET 8 兼容）或 app.MapStaticAssets()（.NET 9+ 推荐）。
        var program = File.ReadAllText(ProgramPath);

        var usesStaticWebAssets = program.Contains("UseStaticWebAssets()");
        var usesMapStaticAssets = program.Contains("MapStaticAssets()");

        (usesStaticWebAssets || usesMapStaticAssets).Should().BeTrue(
            "Program.cs 必须调用 app.UseStaticWebAssets() 或 app.MapStaticAssets()，"
            + "否则 .NET 9+ StaticWebAssets 模式下 wwwroot 资源会 404，"
            + "导致 'isMobileBrowser is not a function'、AppState.IsMobile 永远 false，"
            + "移动端样式不生效。");
    }

    [Fact]
    public void ShutdownEndpoint_ShouldStopHostAfterResponseCompletes()
    {
        var program = File.ReadAllText(ProgramPath);

        program.Should().Contain("context.Response.OnCompleted");
        program.Should().NotContain("Task.Run(async ()");
    }

    [Fact]
    public void SessionCommandDropdown_Override_ShouldRemainOverflowVisible()
    {
        var sessionCss = File.ReadAllText(Path.Combine(WebUiPath, "wwwroot", "css", "session-page.css"));
        var todoCss = File.ReadAllText(Path.Combine(WebUiPath, "wwwroot", "css", "todo.css"))
            .Replace("\r\n", "\n");

        sessionCss.Should().Contain(".session-content-wrapper");
        sessionCss.Should().Contain(".session-main-column");
        todoCss.Should().Contain(".session-content-wrapper");
        todoCss.Should().Contain(".session-main-column");
        todoCss.Should().Contain("overflow: visible;");
        todoCss.Should().NotContain(".session-main-column {\n    flex: 1;\n    min-width: 0;\n    min-height: 0;\n    display: flex;\n    flex-direction: column;\n    overflow: hidden;");
        todoCss.Should().Contain(".session-input-container {\n    position: relative;\n    z-index: 1000;");
        todoCss.Should().Contain(".command-dropdown {\n    z-index: 10000 !important;");
    }

    [Fact]
    public void SessionCommandDropdown_ShouldBeAboveMessageStackingLayers()
    {
        var sessionCss = File.ReadAllText(Path.Combine(WebUiPath, "wwwroot", "css", "session-page.css"));
        var todoCss = File.ReadAllText(Path.Combine(WebUiPath, "wwwroot", "css", "todo.css"))
            .Replace("\r\n", "\n");

        sessionCss.Should().Contain(".session-input-container");
        sessionCss.Should().Contain("z-index: 20;");
        todoCss.Should().Contain(".session-input-container");
        todoCss.Should().Contain("z-index: 1000;");
        todoCss.Should().Contain(".prompt-input-container,\n.command-autocomplete-wrapper");
        todoCss.Should().Contain(".command-dropdown");
        todoCss.Should().Contain("z-index: 10000 !important;");
    }

    [Fact]
    public void SidebarShutdown_ShouldReserveCollapseTriggerSpace()
    {
        var sidebarCss = File.ReadAllText(Path.Combine(WebUiPath, "wwwroot", "css", "sidebar.css"));

        sidebarCss.Should().Contain(".sider-shutdown");
        sidebarCss.Should().Contain("margin-bottom: 48px;");
    }
}
