using System.Runtime.InteropServices;
using System.Text;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 优先经 <see cref="ISubprocess"/> 调用 ripgrep（<c>rg</c>）；不可用时回退托管枚举 + <see cref="IFileSystem"/>。
/// </summary>
internal static class RipgrepSearch
{
    private static readonly object s_probeGate = new();
    private static bool s_probed;
    private static bool s_rgAvailable;

    /// <summary>
    /// 探测当前执行世界是否能启动 <c>rg</c>（结果进程内缓存）。
    /// </summary>
    public static bool IsRipgrepAvailable(ISubprocessFactory subprocess)
    {
        lock (s_probeGate)
        {
            if (s_probed)
                return s_rgAvailable;

            s_probed = true;
            try
            {
                using var proc = subprocess.Start(new SubprocessSpec
                {
                    FileName = "rg",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                proc.WaitForExitAsync().GetAwaiter().GetResult();
                s_rgAvailable = proc.ExitCode == 0;
            }
            catch
            {
                s_rgAvailable = false;
            }

            return s_rgAvailable;
        }
    }

    /// <summary>内容搜索：rg 可用则走子进程，否则托管 GrepSearch。</summary>
    public static List<GrepMatch> Grep(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        string? includePattern,
        int limit)
    {
        if (IsRipgrepAvailable(world.Subprocess))
        {
            try
            {
                var viaRg = GrepViaRipgrep(world.Subprocess, searchPath, pattern, includePattern, limit);
                if (viaRg is not null)
                    return viaRg;
            }
            catch
            {
                // fall through to managed
            }
        }

        return FileSystemHelper.GrepSearch(world.FileSystem, searchPath, pattern, includePattern, limit);
    }

    /// <summary>Glob：rg --files 可用则走子进程，否则托管 GlobSearch。</summary>
    public static List<string> Glob(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        int limit)
    {
        if (IsRipgrepAvailable(world.Subprocess))
        {
            try
            {
                var viaRg = GlobViaRipgrep(world.Subprocess, searchPath, pattern, limit);
                if (viaRg is not null)
                    return viaRg;
            }
            catch
            {
                // fall through to managed
            }
        }

        return FileSystemHelper.GlobSearch(world.FileSystem, searchPath, pattern, limit);
    }

    /// <summary>测试用：重置 rg 探测缓存。</summary>
    internal static void ResetProbeForTests()
    {
        lock (s_probeGate)
        {
            s_probed = false;
            s_rgAvailable = false;
        }
    }

    private static List<GrepMatch>? GrepViaRipgrep(
        ISubprocessFactory subprocess,
        string searchPath,
        string pattern,
        string? includePattern,
        int limit)
    {
        var args = new StringBuilder();
        args.Append("--line-number --color never --no-heading ");
        args.Append("--max-count ").Append(Math.Max(1, limit)).Append(' ');
        if (!string.IsNullOrWhiteSpace(includePattern))
        {
            args.Append("-g ").Append(Quote(includePattern)).Append(' ');
        }

        args.Append("-- ").Append(Quote(pattern)).Append(' ').Append(Quote(searchPath));

        using var proc = subprocess.Start(new SubprocessSpec
        {
            FileName = "rg",
            Arguments = args.ToString(),
            WorkingDirectory = searchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExitAsync().GetAwaiter().GetResult();

        // rg: 0=matches, 1=no matches, 2=error
        if (proc.ExitCode > 1)
            return null;

        var matches = new List<GrepMatch>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (matches.Count >= limit)
                break;

            var first = line.IndexOf(':');
            if (first <= 0)
                continue;
            var second = line.IndexOf(':', first + 1);
            if (second <= first)
                continue;

            var path = line[..first];
            if (!int.TryParse(line[(first + 1)..second], out var lineNum))
                continue;
            var text = line[(second + 1)..];

            matches.Add(new GrepMatch
            {
                Path = path,
                LineNum = lineNum,
                LineText = text,
                ModTime = 0
            });
        }

        return matches;
    }

    private static List<string>? GlobViaRipgrep(
        ISubprocessFactory subprocess,
        string searchPath,
        string pattern,
        int limit)
    {
        var args = new StringBuilder();
        args.Append("--files --color never ");
        args.Append("-g ").Append(Quote(pattern)).Append(' ');
        args.Append(Quote(searchPath));

        using var proc = subprocess.Start(new SubprocessSpec
        {
            FileName = "rg",
            Arguments = args.ToString(),
            WorkingDirectory = searchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExitAsync().GetAwaiter().GetResult();

        if (proc.ExitCode > 1)
            return null;

        var files = stdout
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(limit)
            .ToList();

        return files;
    }

    private static string Quote(string value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
