using System.Net;
using System.Net.Sockets;
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

    private static string WebUiExePath =>
        Path.Combine(WebUiPath, "bin", "Debug", "net10.0", "Seeing.Agent.WebUI.exe");

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

    [Fact]
    public async Task WebUI_StaticAsset_AppJs_Should_Return_200()
    {
        // 集成验证：启动 WebUI 后，js/app.js 必须返回 200 且包含 isMobileBrowser。
        // 这条测试在 exe 未构建时跳过，避免阻塞 CI。
        if (!File.Exists(WebUiExePath))
            return;

        var port = FindAvailablePort();
        using var process = StartWebUi(port);
        try
        {
            await WaitForServerAsync(port, TimeSpan.FromSeconds(30));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetAsync($"http://127.0.0.1:{port}/js/app.js");
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "js/app.js 必须 200，否则 13 处 JS 互操作全部失败");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("isMobileBrowser",
                "js/app.js 响应必须包含 isMobileBrowser 函数定义");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }


    [Fact]
    public async Task WebUI_ShutdownEndpoint_ShouldStopCurrentProcess()
    {
        // 集成验证：关闭接口必须只关闭当前 WebUI 进程。
        if (!File.Exists(WebUiExePath))
            return;

        var port = FindAvailablePort();
        using var process = StartWebUi(port);
        try
        {
            await WaitForServerAsync(port, TimeSpan.FromSeconds(30));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.PostAsync($"http://127.0.0.1:{port}/api/webui/shutdown", content: null);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            process.HasExited.Should().BeTrue("关闭接口应退出当前 WebUI 进程");
            process.ExitCode.Should().Be(0, "正常关闭不应因 hosted service 停止异常而以失败退出码结束");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }
    }

    private static System.Diagnostics.Process StartWebUi(int port)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = WebUiExePath,
            Arguments = $"--urls http://127.0.0.1:{port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(WebUiExePath)!,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        var p = System.Diagnostics.Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[webui] {e.Data}"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[webui-err] {e.Data}"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task WaitForServerAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var r = await http.GetAsync($"http://127.0.0.1:{port}/");
                if (r.StatusCode == HttpStatusCode.OK) return;
                last = new InvalidOperationException($"status={(int)r.StatusCode}");
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(500);
        }
        throw new TimeoutException(
            $"WebUI 未在 {timeout.TotalSeconds:F0}s 内监听 :{port}。最后一次错误: {last?.Message}");
    }
}
