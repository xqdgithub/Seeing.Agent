using System.Diagnostics;

namespace Seeing.Agent.Cli.Services;

/// <summary>
/// 前台子进程运行器：子进程继承当前终端（不重定向、不新建窗口），
/// 父进程吞掉 Ctrl+C 自身信号、把终止交给子进程，随后阻塞等待其退出。
/// </summary>
internal static class ForegroundProcessRunner
{
    /// <summary>Kill 未生效时的兜底等待上限（避免父进程无限挂起）。</summary>
    private static readonly TimeSpan KillFallbackWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 构造前台启动信息。与 <see cref="ServiceProcessManager.CreateStartInfo"/> 的区别：
    /// 不重定向三个标准流、不隐藏窗口，使子进程直接占用当前终端。
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(
        string dllPath,
        string workingDirectory,
        IEnumerable<string>? arguments = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = workingDirectory,
        };

        psi.ArgumentList.Add(dllPath);
        if (arguments is not null)
        {
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var entry in environment)
                psi.Environment[entry.Key] = entry.Value;
        }

        return psi;
    }

    /// <summary>
    /// 等待子进程退出并返回其退出码。
    /// Ctrl+C 只拦截父进程自身（<c>e.Cancel = true</c>），子进程仍按自身语义处理该信号。
    /// </summary>
    /// <param name="killOnInterruptAfter">
    /// 收到 Ctrl+C 后等待子进程自行退出的宽限期，超时则强制结束进程树。
    /// 传 <c>null</c> 表示永不代为终止——交互式 TUI 在 raw 模式下根本不产生 CTRL_C_EVENT，
    /// 退出确认也由 TUI 自己负责，父进程不得抢杀。
    /// </param>
    public static async Task<int> RunAsync(
        Process process,
        TimeSpan? killOnInterruptAfter = null,
        CancellationToken ct = default)
    {
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;   // 父进程不退出，由子进程决定何时结束
            interrupted.TrySetResult();
        };

        Console.CancelKeyPress += handler;
        try
        {
            var exitTask = process.WaitForExitAsync(ct);

            if (killOnInterruptAfter is null)
            {
                await exitTask.ConfigureAwait(false);
            }
            else
            {
                var first = await Task.WhenAny(exitTask, interrupted.Task).ConfigureAwait(false);
                if (first == interrupted.Task && !process.HasExited)
                {
                    var grace = await Task.WhenAny(exitTask, Task.Delay(killOnInterruptAfter.Value, ct))
                        .ConfigureAwait(false);
                    if (grace != exitTask && !process.HasExited)
                    {
                        TryKill(process);

                        // Kill 可能因权限或进程已脱离而失败，此处必须有界兜底，不能无限等待。
                        if (!process.HasExited
                            && await Task.WhenAny(exitTask, Task.Delay(KillFallbackWait, ct)).ConfigureAwait(false) != exitTask)
                        {
                            Console.Error.WriteLine(
                                $"子进程 {process.Id} 未响应终止请求，请手动结束该进程。");
                            return 1;
                        }
                    }
                }

                await exitTask.ConfigureAwait(false);
            }

            return process.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已自行退出；若因其它原因失败，交由上层的有界兜底处理。
        }
    }
}
