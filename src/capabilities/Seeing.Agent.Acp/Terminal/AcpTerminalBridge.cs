using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Acp.Messages;
using Acp.Types;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Acp.Terminal;

/// <summary>
/// ACP Terminal 回调桥接（基于 Process 的基础实现）。
/// </summary>
public sealed class AcpTerminalBridge : IAsyncDisposable
{
    /// <summary>进程退出后保留输出/句柄的默认宽限期。</summary>
    public static readonly TimeSpan DefaultExitRetention = TimeSpan.FromMinutes(5);

    /// <summary>终端长时间无访问的默认兜底回收阈值。</summary>
    public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(30);

    private sealed class TerminalEntry
    {
        public required string SessionId { get; init; }
        public required Process Process { get; init; }
        public StringBuilder Output { get; } = new();
        public bool Exited { get; set; }
        public int ExitCode { get; set; }
        public DateTime LastTouchedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ExitedAtUtc { get; set; }
        public int Released;
    }

    private readonly ConcurrentDictionary<string, TerminalEntry> _terminals = new();
    private readonly ILogger<AcpTerminalBridge> _logger;
    private readonly TimeSpan _exitRetention;
    private readonly TimeSpan _idleTtl;

    public AcpTerminalBridge(
        ILogger<AcpTerminalBridge> logger,
        TimeSpan? exitRetention = null,
        TimeSpan? idleTtl = null)
    {
        _logger = logger;
        _exitRetention = exitRetention ?? DefaultExitRetention;
        _idleTtl = idleTtl ?? DefaultIdleTtl;
    }

    /// <summary>当前仍被桥接持有的终端数量（供诊断与测试）。</summary>
    public int ActiveTerminalCount => _terminals.Count;

    public Task<CreateTerminalResponse> CreateTerminalAsync(
        string command,
        string sessionId,
        string workingDirectory,
        List<string>? args = null,
        List<EnvVariable>? env = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var terminalId = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args is { Count: > 0 } ? string.Join(' ', args) : "",
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (env != null)
        {
            foreach (var variable in env)
            {
                if (!string.IsNullOrWhiteSpace(variable.Name))
                    startInfo.Environment[variable.Name] = variable.Value ?? "";
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var entry = new TerminalEntry { SessionId = sessionId ?? "", Process = process };
        _terminals[terminalId] = entry;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                entry.Output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                entry.Output.AppendLine(e.Data);
        };
        process.Exited += (_, _) =>
        {
            entry.Exited = true;
            try
            {
                entry.ExitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取 ACP 终端退出码失败 {TerminalId}", terminalId);
            }

            entry.ExitedAtUtc = DateTime.UtcNow;
            // 退出后不立即清理：先保留输出供上层读取，宽限期到达后再回收 Process + Output
            ScheduleExitCleanup(terminalId, entry);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 顺带兜底回收既已超期（未退出且长时间无访问）的终端
        SweepExpired();

        _logger.LogDebug("Created ACP terminal {TerminalId} for session {SessionId}", terminalId, sessionId);
        return Task.FromResult(new CreateTerminalResponse { TerminalId = terminalId });
    }

    public Task<TerminalOutputResponse> TerminalOutputAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_terminals.TryGetValue(terminalId, out var entry))
            return Task.FromResult(new TerminalOutputResponse { Exited = true, Stdout = "" });

        entry.LastTouchedUtc = DateTime.UtcNow;
        return Task.FromResult(new TerminalOutputResponse
        {
            Exited = entry.Exited || SafeHasExited(entry),
            Stdout = entry.Output.ToString()
        });    }

    public Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        if (!_terminals.TryGetValue(terminalId, out var entry))
            return Task.FromResult(new WaitForTerminalExitResponse { ExitCode = -1 });

        entry.LastTouchedUtc = DateTime.UtcNow;
        return WaitForExitInternalAsync(entry, cancellationToken);
    }

    public Task<KillTerminalCommandResponse?> KillTerminalAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        if (_terminals.TryGetValue(terminalId, out var entry) && !SafeHasExited(entry))
        {
            try
            {
                entry.Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "终止 ACP 终端进程失败 {TerminalId}", terminalId);
            }
        }

        return Task.FromResult<KillTerminalCommandResponse?>(new KillTerminalCommandResponse { Killed = true });
    }

    public Task<ReleaseTerminalResponse?> ReleaseTerminalAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        if (_terminals.TryGetValue(terminalId, out var entry))
            Release(terminalId, entry);

        return Task.FromResult<ReleaseTerminalResponse?>(new ReleaseTerminalResponse { Released = true });
    }

    /// <summary>
    /// 释放指定会话名下的全部终端（Session 销毁时级联清理）。
    /// </summary>
    /// <returns>被释放的终端数量。</returns>
    public int ReleaseBySession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        var released = 0;
        foreach (var (terminalId, entry) in _terminals)
        {
            if (string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
            {
                Release(terminalId, entry);
                released++;
            }
        }

        return released;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var terminalId in _terminals.Keys.ToList())
            await ReleaseTerminalAsync("", terminalId).ConfigureAwait(false);
    }

    private async Task<WaitForTerminalExitResponse> WaitForExitInternalAsync(
        TerminalEntry entry,
        CancellationToken cancellationToken)
    {
        if (!SafeHasExited(entry))
            await entry.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        entry.Exited = true;
        try
        {
            entry.ExitCode = entry.Process.ExitCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 ACP 终端退出码失败");
        }

        return new WaitForTerminalExitResponse { ExitCode = entry.ExitCode };
    }

    private void ScheduleExitCleanup(string terminalId, TerminalEntry entry)
    {
        if (_exitRetention <= TimeSpan.Zero)
        {
            Release(terminalId, entry);
            return;
        }

        _ = Task.Delay(_exitRetention)
            .ContinueWith(
                _ => Release(terminalId, entry),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void Release(string terminalId, TerminalEntry entry)
    {
        // 只释放一次，避免 Exited 定时器与显式 Release 双释放
        if (Interlocked.Exchange(ref entry.Released, 1) != 0)
            return;

        if (!_terminals.TryRemove(new KeyValuePair<string, TerminalEntry>(terminalId, entry)))
            return;

        try
        {
            if (!SafeHasExited(entry))
                entry.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "回收 ACP 终端进程失败 {TerminalId}", terminalId);
        }

        entry.Process.Dispose();
    }

    /// <summary>兜底回收：已退出超宽限期，或未退出但空闲超 TTL 的终端。</summary>
    internal int SweepExpired()
    {
        var now = DateTime.UtcNow;
        var released = 0;

        foreach (var (terminalId, entry) in _terminals)
        {
            var expired = entry.ExitedAtUtc is { } exitedAt
                ? now - exitedAt > _exitRetention
                : now - entry.LastTouchedUtc > _idleTtl;

            if (expired)
            {
                Release(terminalId, entry);
                released++;
            }
        }

        return released;
    }

    private bool SafeHasExited(TerminalEntry entry)
    {
        try
        {
            return entry.Process.HasExited;
        }
        catch (Exception ex)
        {
            // 进程句柄可能已被回收（视为已退出）
            _logger.LogDebug(ex, "探测 ACP 终端进程退出状态失败，按已退出处理");
            return true;
        }
    }
}
