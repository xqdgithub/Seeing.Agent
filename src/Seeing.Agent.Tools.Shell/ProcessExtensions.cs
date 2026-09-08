using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seeing.Agent.Tools.Shell;

/// <summary>
/// 进程扩展方法 - 提供跨平台进程树终止功能
/// </summary>
public static class ProcessExtensions
{
    private const int SigKillTimeoutMs = 200;

    /// <summary>
    /// 终止进程树（包括所有子进程）
    /// </summary>
    public static async Task KillTreeAsync(this Process process, CancellationToken cancellationToken = default)
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        var pid = process.Id;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await KillTreeWindowsAsync(pid, cancellationToken);
        }
        else
        {
            await KillTreeUnixAsync(process, pid, cancellationToken);
        }
    }

    private static async Task KillTreeWindowsAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            var tcs = new TaskCompletionSource<bool>();

            using var killer = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/pid {pid} /f /t",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            killer.Exited += (s, e) => tcs.TrySetResult(true);

            killer.Start();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await tcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult(true);
            }
        }
        catch (Exception)
        {
            // 忽略终止进程时的异常
        }
    }

    private static async Task KillTreeUnixAsync(Process process, int pid, CancellationToken cancellationToken)
    {
        try
        {
            SendSignalToProcessGroup(-pid, "TERM");

            await Task.Delay(SigKillTimeoutMs, cancellationToken);

            if (!process.HasExited)
            {
                SendSignalToProcessGroup(-pid, "KILL");
            }
        }
        catch (Exception)
        {
            try
            {
                process.Kill();
                await Task.Delay(SigKillTimeoutMs, cancellationToken);

                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // 忽略终止进程时的异常
            }
        }
    }

    private static void SendSignalToProcessGroup(int pgid, string signal)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                using var killProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-{signal} {Math.Abs(pgid)}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                killProcess?.WaitForExit(100);
            }
            catch (Exception)
            {
                // 忽略错误
            }
        }
    }
}
