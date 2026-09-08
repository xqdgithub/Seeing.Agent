using System.Diagnostics;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.IO.Local;

/// <summary>
/// 包装 <see cref="Process"/> 的本地子进程句柄。
/// </summary>
public sealed class LocalSubprocess : ISubprocess
{
    private readonly Process _process;

    public LocalSubprocess(Process process)
    {
        _process = process;
    }

    /// <inheritdoc />
    public int Id => _process.Id;

    /// <inheritdoc />
    public TextReader StandardOutput => _process.StandardOutput;

    /// <inheritdoc />
    public TextReader StandardError => _process.StandardError;

    /// <inheritdoc />
    public bool HasExited => _process.HasExited;

    /// <inheritdoc />
    public int ExitCode => _process.ExitCode;

    /// <inheritdoc />
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _process.WaitForExitAsync(cancellationToken);

    /// <inheritdoc />
    public void Kill(bool entireTree = true) => _process.Kill(entireTree);

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();
}
