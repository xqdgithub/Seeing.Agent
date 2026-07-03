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
    private sealed class TerminalEntry
    {
        public required Process Process { get; init; }
        public StringBuilder Output { get; } = new();
        public bool Exited { get; set; }
        public int ExitCode { get; set; }
    }

    private readonly ConcurrentDictionary<string, TerminalEntry> _terminals = new();
    private readonly ILogger<AcpTerminalBridge> _logger;

    public AcpTerminalBridge(ILogger<AcpTerminalBridge> logger)
    {
        _logger = logger;
    }

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
        var entry = new TerminalEntry { Process = process };
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
            entry.ExitCode = process.ExitCode;
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

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

        return Task.FromResult(new TerminalOutputResponse
        {
            Exited = entry.Exited || entry.Process.HasExited,
            Stdout = entry.Output.ToString()
        });
    }

    public Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        if (!_terminals.TryGetValue(terminalId, out var entry))
            return Task.FromResult(new WaitForTerminalExitResponse { ExitCode = -1 });

        return WaitForExitInternalAsync(entry, cancellationToken);
    }

    public Task<KillTerminalCommandResponse?> KillTerminalAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        if (_terminals.TryGetValue(terminalId, out var entry) && !entry.Process.HasExited)
        {
            try { entry.Process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        return Task.FromResult<KillTerminalCommandResponse?>(new KillTerminalCommandResponse { Killed = true });
    }

    public Task<ReleaseTerminalResponse?> ReleaseTerminalAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        if (_terminals.TryRemove(terminalId, out var entry))
        {
            try
            {
                if (!entry.Process.HasExited)
                    entry.Process.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            entry.Process.Dispose();
        }

        return Task.FromResult<ReleaseTerminalResponse?>(new ReleaseTerminalResponse { Released = true });
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
        if (!entry.Process.HasExited)
            await entry.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        entry.Exited = true;
        entry.ExitCode = entry.Process.ExitCode;
        return new WaitForTerminalExitResponse { ExitCode = entry.ExitCode };
    }
}
