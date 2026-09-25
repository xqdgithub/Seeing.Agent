using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// 基于 <see cref="Process.Start(ProcessStartInfo)"/> 的默认浏览器打开器（跨平台）。
    /// <para>
    /// 使用 <c>UseShellExecute=true</c> 交由操作系统处理：Windows 走默认浏览器，
    /// macOS 走 open，Linux 走 xdg-open。打开失败时记录日志并返回 false。
    /// </para>
    /// </summary>
    public sealed class SystemBrowserLauncher : IBrowserLauncher
    {
        private readonly ILogger<SystemBrowserLauncher> _logger;

        public SystemBrowserLauncher(ILogger<SystemBrowserLauncher> logger) => _logger = logger;

        /// <inheritdoc />
        public bool TryOpen(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                _logger.LogWarning("授权 URL 无效，无法打开浏览器");
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "打开系统浏览器失败，需用户手动访问授权 URL");
                return false;
            }
        }
    }
}
