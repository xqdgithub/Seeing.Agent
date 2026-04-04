using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seeing.Agent.Tools.BuiltIn.Shell
{
    /// <summary>
    /// 进程扩展方法 - 提供跨平台进程树终止功能
    /// </summary>
    public static class ProcessExtensions
    {
        private const int SigKillTimeoutMs = 200;

        /// <summary>
        /// 终止进程树（包括所有子进程）
        /// </summary>
        /// <param name="process">要终止的进程</param>
        /// <param name="cancellationToken">取消令牌</param>
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

        /// <summary>
        /// Windows 平台终止进程树
        /// </summary>
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
                
                // 设置超时以防进程启动失败
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

        /// <summary>
        /// Unix 平台终止进程树
        /// </summary>
        private static async Task KillTreeUnixAsync(Process process, int pid, CancellationToken cancellationToken)
        {
            try
            {
                // 尝试终止进程组（使用负 PID）
                SendSignalToProcessGroup(-pid, "TERM");

                // 等待一段时间后检查进程是否退出
                await Task.Delay(SigKillTimeoutMs, cancellationToken);

                if (!process.HasExited)
                {
                    // 如果进程还在运行，强制终止
                    SendSignalToProcessGroup(-pid, "KILL");
                }
            }
            catch (Exception)
            {
                // 如果进程组终止失败，尝试直接终止进程
                try
                {
                    process.Kill();
                    await Task.Delay(SigKillTimeoutMs, cancellationToken);

                    if (!process.HasExited)
                    {
                        // 再次尝试强制终止
                        process.Kill();
                    }
                }
                catch (Exception)
                {
                    // 忽略终止进程时的异常
                }
            }
        }

        /// <summary>
        /// 向进程组发送信号
        /// </summary>
        private static void SendSignalToProcessGroup(int pgid, string signal)
        {
            // 使用 kill 命令发送信号到进程组
            // pgid 应该是负数，表示进程组 ID
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
}