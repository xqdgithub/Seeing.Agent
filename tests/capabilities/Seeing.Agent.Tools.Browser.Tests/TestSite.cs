using System.Net;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 测试用本地 HTTP 站点：固定返回一个带输入框的页面，可 await 干净关闭。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么抽成类型而不是每处 <c>_ = Task.Run(...)</c></b>：早期写法把后台泵任务丢弃，
/// 清理时只 <c>listener.Stop()</c> 而不等任务退出——测试结束与后台泵存在竞态，
/// 表现为偶发的 <c>ObjectDisposedException</c> 或端口占用（下一个用例绑不上）。
/// 这里持有任务并实现 <see cref="IAsyncDisposable"/>，关闭时明确等它收敛。
/// </para>
/// <para>
/// 泵里的 <c>Task.Delay</c> 属 AGENTS.md 例外表 E3（真实外部 IO 泵：事件来源在测试外，
/// 无注入点），且仅在响应时使用，不是「等状态就绪」型等待。
/// </para>
/// </remarks>
internal sealed class TestSite : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _pump;

    private TestSite(HttpListener listener, Task pump, int port)
    {
        _listener = listener;
        _pump = pump;
        Port = port;
    }

    /// <summary>站点监听端口。</summary>
    public int Port { get; }

    /// <summary>站点根地址（<c>http://127.0.0.1:{Port}/</c>）。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>启动站点。</summary>
    public static Task<TestSite> StartAsync() => Task.FromResult(Start());

    /// <summary>启动站点（同步版，供 <c>using</c> 场景）。</summary>
    public static TestSite Start()
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var pump = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    // 正常关闭路径：Stop()/Close() 会让挂起的 GetContextAsync 抛这些
                    return;
                }

                try
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(PageHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
                {
                    // 客户端已断开 / 监听器已关闭：继续泵或退出由外层循环判定
                }
            }
        });

        return new TestSite(listener, pump, port);
    }

    /// <summary>页面内容：普通文本框、密码框、以及可滚动区域。</summary>
    private const string PageHtml = """
        <html><body style="margin:0;background:#fff">
        <input id="name" type="text" style="position:absolute;left:10px;top:10px">
        <input id="pwd" type="password" style="position:absolute;left:10px;top:50px">
        <div style="position:absolute;left:0;top:100px;width:500px;height:900px;background:#eee"></div>
        </body></html>
        """;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }

        // 等泵真正退出：不等的话下一个用例可能与残留请求处理交错
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // 泵未在期限内退出：不影响断言，让它随进程回收
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
