using System.Diagnostics;

namespace Seeing.Agent.Cli.Services;

internal static class BrowserLauncher
{
    public static bool TryOpen(string url, out string? error)
        => TryOpen(url, starter: null, out error);

    internal static bool TryOpen(
        string url,
        Func<ProcessStartInfo, bool>? starter,
        out string? error)
    {
        try
        {
            var startInfo = CreateStartInfo(url);
            var started = starter is not null
                ? starter(startInfo)
                : Process.Start(startInfo) is not null;
            error = started ? null : "系统未能创建浏览器进程";
            return started;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
        }

        var command = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }
}
