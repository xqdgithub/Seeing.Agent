using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seeing.Agent.Hooks.Directory.Scripts;

public sealed class SystemNodeRuntime : INodeRuntime
{
    private static readonly TimeSpan ProbeTimeout = NodeHookClient.HookScriptTimeout;
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(2);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public SystemNodeRuntime(TimeProvider? time = null, ILogger? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool Probe()
    {
        Process? process = null;
        try
        {
            process = Process.Start(CreateStartInfo(redirectInput: false));
            if (process is null)
                return false;

            return ProbeStarted(process);
        }
        catch (Win32Exception)
        {
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public INodeProcess StartWorker(string workerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);

        var startInfo = CreateStartInfo(redirectInput: true);
        startInfo.ArgumentList.Add(workerPath);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("未能启动 node。");
        process.StandardInput.NewLine = "\n";
        return new SystemNodeProcess(process, _time, _logger);
    }

    private static bool ProbeStarted(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProbeTimeout))
        {
            TryKill(process);
            WaitDrain(stdout);
            WaitDrain(stderr);
            return false;
        }

        WaitDrain(stdout);
        WaitDrain(stderr);
        return process.ExitCode == 0;
    }

    private static ProcessStartInfo CreateStartInfo(bool redirectInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (redirectInput)
            startInfo.StandardInputEncoding = Utf8;
        else
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("process.exit(0)");
        }

        return startInfo;
    }

    private static void WaitDrain(Task drain)
    {
        try
        {
            if (!drain.Wait(TimeSpan.FromSeconds(1)))
                _ = drain.ContinueWith(static completed => _ = completed.Exception, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is AggregateException or IOException or ObjectDisposedException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
        }
    }

    private sealed class SystemNodeProcess : INodeProcess
    {
        private readonly Process _process;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private int _disposed;

        public SystemNodeProcess(Process process, TimeProvider time, ILogger logger)
        {
            _process = process;
            _time = time;
            _logger = logger;
            _ = PumpStderrAsync();
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        public void Kill() => TryKill(_process);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "关闭 Node 工人 stdin 失败。");
            }

            using var timeout = new CancellationTokenSource(DisposeGrace, _time);
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(_process);
            }

            _process.Dispose();
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                while (true)
                {
                    var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                        break;
                    _logger.LogDebug("Node 工人 stderr: {Line}", line);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取 Node 工人 stderr 结束。");
            }
        }
    }
}
