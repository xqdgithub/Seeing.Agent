using Seeing.Agent.Abstractions.Execution;
using System.Diagnostics;

namespace Seeing.IO.Local;

/// <summary>
/// 基于 <see cref="Process.Start"/> 的本地子进程工厂。
/// </summary>
public sealed class LocalSubprocessFactory : ISubprocessFactory
{
    /// <inheritdoc />
    public ISubprocess Start(SubprocessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(spec.FileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = spec.RedirectStandardOutput,
            RedirectStandardError = spec.RedirectStandardError,
            StandardOutputEncoding = spec.Encoding,
            StandardErrorEncoding = spec.Encoding,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (spec.ArgumentList is { Count: > 0 })
        {
            foreach (var argument in spec.ArgumentList)
                startInfo.ArgumentList.Add(argument);
        }
        else
        {
            startInfo.Arguments = spec.Arguments;
        }

        foreach (var (key, value) in spec.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{spec.FileName}'.");

        return new LocalSubprocess(process);
    }
}
