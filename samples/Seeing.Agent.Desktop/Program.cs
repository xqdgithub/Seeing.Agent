using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using Photino.NET;
using Seeing.Agent.Desktop.Services;
using Seeing.Agent.WebUI.Hosting;

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
        // 工作区必须在宿主组装之前确定：
        // WorkspaceProvider 构造时就把「进程当前目录」记成 StartupDirectory，
        // 之后再改 cwd 不会更新它，兜底路径会一直指向旧的启动目录。
        // 桌面启动的 cwd 通常是 exe 目录（Program Files，不可写），不能当工作区。
        //
        // 这里只摆正「起始位置」，不裁决最终工作区：
        // 最终工作区仍由 WorkspaceProvider 按 Core 优先级决定
        // （环境变量 → 项目 CustomPath → UseGlobal 为真时的全局目录 → 启动目录）。
        var startLocation = DesktopWorkspace.Apply();
        Console.WriteLine($"起始位置: {startLocation.Path}（来源: {startLocation.Describe()}）");

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
                app = WebUiHost.BuildAsync(hostArgs, AppContext.BaseDirectory)
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
            Console.Error.WriteLine($"WebUI 宿主启动失败：{startupError?.Message}");
            return 1;
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
