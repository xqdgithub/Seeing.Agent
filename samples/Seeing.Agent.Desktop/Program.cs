using Photino.NET;
using Seeing.Agent.Desktop.Services;
using Seeing.Agent.WebUI.Hosting;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;

namespace Seeing.Agent.Desktop;

/// <summary>
/// 桌面宿主：在同一个进程里启动 WebUI（Kestrel + Blazor Server），
/// 再用系统原生 WebView 窗口加载它。
/// <para>
/// 之所以不是 Blazor Hybrid：本应用是 Blazor <b>Server</b>（SignalR circuit），
/// 页面/CSS/JS 与 circuit 作用域服务都按「浏览器访问 URL」模型编写。
/// 复用同一套宿主组装 + 原生窗口指向本地地址，可以零改动复用既有 UI。
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>窗口标题。</summary>
    private const string WindowTitle = "Seeing";

    /// <summary>应用名（PhotinoX 应用级标识，Windows 上用于任务栏/通知归属）。</summary>
    private const string ApplicationName = "Seeing";

    /// <summary>窗口图标文件名（随输出复制，见 csproj）。</summary>
    private const string IconFileName = "seeing.ico";

    /// <summary>首选端口；与 CLI 的 WebUI 起始端口保持一致，避免两套习惯。</summary>
    private const int PreferredPort = 7658;

    [STAThread]
    private static int Main(string[] args)
    {
        // 捕获用户真正的启动位置：Apply() 之后进程当前目录会变，
        // 而启动决议要用的正是「双击/终端里启动时」的目录。
        var captureStartupDirectory = Directory.GetCurrentDirectory();

        // 这里只摆正「起始位置」，不裁决最终工作区：
        // 最终工作区由 WorkspaceLaunchPolicy 在宿主组装时按启动决议决定。
        var startLocation = DesktopWorkspace.Apply();

        // WinExe 没有控制台，日志必须落盘，否则出问题无从排查。
        var logProvider = new DesktopFileLoggerProvider(DesktopFileLoggerProvider.ResolveLogDirectory());

        var port = FindAvailablePort(PreferredPort);
        var url = $"http://127.0.0.1:{port}";

        // 桌面形态的两个约束，全部通过命令行参数传给宿主（不改进程环境变量）：
        //   1. 只监听回环地址，避免把本地 Agent 暴露到局域网；
        //   2. 关闭 HTTPS 跳转 —— 否则 WebView 会落到不受信任的开发证书页。
        //
        // 不能用 Environment.SetEnvironmentVariable：WebUI 会派生 ChannelHost/Gateway 等子进程，
        // 子进程继承环境变量后会把 ASPNETCORE_URLS 一并带走，污染它们的监听地址。
        var hostArgs = new[]
        {
            "--urls", url,
            "--DisableHttpsRedirection=true",
        };

        // 先起 Kestrel（同进程），再开窗口；窗口关闭时同步停服。
        WebApplication? app = null;
        var hostReady = new ManualResetEventSlim(false);
        var startupError = default(Exception);

        var hostThread = new Thread(() =>
        {
            try
            {
                // 内容根必须指向外壳自己的输出目录（WebUI 的静态资源已随引用复制过来）。
                // 日志切到文件：桌面形态没有控制台窗口。
                app = WebUiHost.BuildAsync(
                        hostArgs,
                        AppContext.BaseDirectory,
                        logging =>
                        {
                            // 清掉默认 Console 提供方（WinExe 下没有控制台，写入是空操作）。
                            logging.ClearProviders();
                            logging.AddProvider(logProvider);
                        },
                        captureStartupDirectory)
                    .GetAwaiter()
                    .GetResult();

                app.StartAsync().GetAwaiter().GetResult();
                hostReady.Set();

                // 阻塞至 StopApplication（窗口关闭时触发）。
                app.WaitForShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                startupError = ex;
                hostReady.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Seeing.Agent.Desktop.Host",
        };
        hostThread.Start();

        hostReady.Wait();
        if (startupError is not null || app is null)
        {
            // WinExe 没有控制台，启动失败必须让用户看得见，否则表现为「双击没反应」。
            // 不用 WinForms 的 MessageBox：那会把目标框架绑到 net10.0-windows，
            // 破坏本项目「同一份代码跨平台」的前提。这里直接用同一个 WebView 显示错误页。
            logProvider.CreateLogger("Seeing.Desktop").LogError(startupError, "宿主启动失败");

            return ShowStartupError(startupError, logProvider.FilePath);
        }

        var application = new PhotinoApplication { NotificationsEnabled = false };

        var window = new PhotinoWindow()
            .SetTitle(WindowTitle)
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, IconFileName))
            .SetSize(new Size(1440, 900))
            .SetMinSize(900, 600)
            .Center()
            .SetUseOsDefaultLocation(false)
            // 状态栏会显示当前 URL（顶部一条黄带），桌面形态不需要。
            .SetStatusBarEnabled(false)
            .SetContextMenuEnabled(true)
            .SetDevToolsEnabled(true)
            // 本地 http 地址：不启用证书忽略，保持默认安全设置。
            .Load(url)
            .RegisterNavigationStartingHandler((_, e) =>
            {
                // 外部链接（文档、模型页等）不在应用窗口内裸跳，
                // 交给系统浏览器，避免窗口被带走且无法返回。
                if (IsExternalUri(e.Uri, url))
                {
                    e.Cancel = true;
                    OpenInSystemBrowser(e.Uri);
                }
            })
            .RegisterNewWindowRequestedHandler((_, e) =>
            {
                // target=_blank / window.open：一律外开，不为外部站点创建第二个窗口。
                if (e.Uri is { } uri && IsExternalUri(uri, url))
                    OpenInSystemBrowser(uri);
            });

        application
            .SetName(ApplicationName)
            .SetShutdownMode(PhotinoShutdownMode.OnMainWindowClose);

        try
        {
            return application.Run(window);
        }
        finally
        {
            // 窗口关闭 → 触发 Host 正常停止（释放 circuit、后台执行与文件句柄）。
            app?.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 用同一个原生窗口展示启动失败原因（WinExe 没有控制台，否则用户只看到「双击没反应」）。
    /// </summary>
    /// <returns>固定返回 1（非零退出码）。</returns>
    private static int ShowStartupError(Exception? error, string logPath)
    {
        var detail = WebUtility.HtmlEncode(error?.ToString() ?? "未知错误");

        var html = $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8" />
              <title>Seeing 启动失败</title>
              <style>
                body { font-family: "Segoe UI", system-ui, sans-serif; margin: 0; padding: 32px;
                       background: #fafafa; color: #1c1c1e; }
                h1 { font-size: 20px; margin: 0 0 12px; color: #cf1322; }
                p  { line-height: 1.7; margin: 8px 0; }
                code { background: #f0f0f0; padding: 2px 6px; border-radius: 4px;
                       font-family: Consolas, monospace; font-size: 13px; }
                pre { background: #fff; border: 1px solid #e8e8e8; border-radius: 6px;
                      padding: 16px; overflow: auto; font-size: 12px; line-height: 1.5;
                      max-height: 320px; white-space: pre-wrap; word-break: break-all; }
              </style>
            </head>
            <body>
              <h1>Seeing 无法启动</h1>
              <p>后台服务启动失败，应用窗口无法打开。</p>
              <p>完整日志：<code>{{WebUtility.HtmlEncode(logPath)}}</code></p>
              <p>错误详情：</p>
              <pre>{{detail}}</pre>
            </body>
            </html>
            """;

        try
        {
            var application = new PhotinoApplication { NotificationsEnabled = false };
            var window = new PhotinoWindow()
                .SetTitle(WindowTitle)
                .SetIconFile(Path.Combine(AppContext.BaseDirectory, IconFileName))
                .SetSize(new Size(900, 620))
                .Center()
                .SetStatusBarEnabled(false)
                .LoadString(html);

            application
                .SetName(ApplicationName)
                .SetShutdownMode(PhotinoShutdownMode.OnMainWindowClose);

            application.Run(window);
        }
        catch
        {
            // 连错误窗口都开不出来时只能返回码，日志里仍有完整堆栈。
        }

        return 1;
    }

    /// <summary>判断是否为应用外部地址（任何非本机回环、或非当前监听端口的地址）。</summary>
    private static bool IsExternalUri(Uri uri, string appUrl)
        => !uri.IsLoopback
           || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
           || uri.Authority != new Uri(appUrl).Authority;

    private static void OpenInSystemBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 打开浏览器失败不影响应用本体；用户仍可手动复制地址。
        }
    }

    /// <summary>
    /// 分配可用端口。竞态说明：探测与实际绑定之间仍可能被抢占，
    /// 因此失败由 Kestrel 绑定异常上抛（宿主启动失败路径）兜底。
    /// </summary>
    private static int FindAvailablePort(int preferred, int maxAttempts = 100)
    {
        for (var offset = 0; offset < maxAttempts; offset++)
        {
            var candidate = preferred + offset;
            if (candidate > IPEndPoint.MaxPort) break;
            if (CanBind(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            $"在 {preferred}~{preferred + maxAttempts - 1} 范围内找不到可用端口");
    }

    private static bool CanBind(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode != SocketError.AddressAlreadyInUse;
        }
    }
}
