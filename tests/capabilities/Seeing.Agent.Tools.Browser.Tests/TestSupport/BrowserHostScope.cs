using Seeing.Agent.Tools.Browser;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 真实宿主的作用域：<c>using</c> 结束时杀掉宿主并删掉临时 profile（等价于 finally 清理）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么统一走这个作用域</b>：共享宿主模式下客户端不再自己 Launch 浏览器，用例必须先有一个
/// 真实宿主。漏掉清理会在测试机留下占着 profile 的浏览器进程，并让下一次运行撞上同一把锁
/// （「The browser is already running for ...」），而且不会有任何提示指向「上一个用例没清干净」。
/// </para>
/// <para>
/// <b>为什么把 <see cref="BrowserOptions"/> 传给 <see cref="TestHost.Start(string, BrowserOptions, int)"/></b>：
/// 浏览器级设置（headless / anti-detection / 可执行文件 / UA / 追加参数）只由宿主持有；
/// 不传就会出现「客户端配 A、宿主跑 B」，测的不是被测行为。
/// </para>
/// <para>
/// <b>为什么暴露 <see cref="Profile"/></b>：客户端引擎要用同一个 profile 解析宿主身份与客户端锁目录。
/// 两边各造一个路径就会变成各自识别一个「宿主」，永远连不上。
/// </para>
/// </remarks>
internal sealed class BrowserHostScope : IDisposable
{
    private bool _disposed;

    /// <summary>启动宿主（同步等待发现文件）。</summary>
    /// <param name="profileDirectory">专用 profile 目录（同时作为发现文件根目录）。</param>
    /// <param name="options">客户端配置；宿主级字段会原样传给宿主 CLI。</param>
    /// <param name="hostIdleSeconds">宿主空闲退出秒数（测试用较短值）。</param>
    public BrowserHostScope(
        string profileDirectory,
        BrowserOptions options,
        int hostIdleSeconds = 300)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentNullException.ThrowIfNull(options);

        Profile = profileDirectory;
        Directory.CreateDirectory(profileDirectory);
        TestHost.Start(profileDirectory, options, hostIdleSeconds);
    }

    /// <summary>宿主的 profile 目录（客户端引擎必须指向它）。</summary>
    public string Profile { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        TestHost.Cleanup(Profile);

        // Chromium 子进程可能在宿主被杀后短暂持有文件句柄：同步重试几次再放弃。
        // （清理失败不影响断言；E5：轮询间隔，命中即返回。）
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (Directory.Exists(Profile))
                    Directory.Delete(Profile, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }

    }
}
