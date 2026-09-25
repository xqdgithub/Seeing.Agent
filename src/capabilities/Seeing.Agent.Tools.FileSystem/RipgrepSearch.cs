using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 优先经 <see cref="ISubprocess"/> 调用 ripgrep（<c>rg</c>）；不可用时回退托管枚举 + <see cref="IFileSystem"/>。
/// </summary>
internal static class RipgrepSearch
{
    /// <summary>探测 rg 可用性的超时。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>单次搜索子进程的超时。</summary>
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim s_probeGate = new(1, 1);
    private static bool s_probed;
    private static bool s_rgAvailable;

    /// <summary>
    /// 探测当前执行世界是否能启动 <c>rg</c>（结果进程内缓存）。
    /// </summary>
    public static async Task<bool> IsRipgrepAvailableAsync(
        ISubprocessFactory subprocess,
        CancellationToken ct = default)
    {
        if (s_probed)
            return s_rgAvailable;

        await s_probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (s_probed)
                return s_rgAvailable;

            try
            {
                using var proc = subprocess.Start(new SubprocessSpec
                {
                    FileName = "rg",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                await RunWithTimeoutAsync(proc, ProbeTimeout, ct).ConfigureAwait(false);
                s_rgAvailable = proc.ExitCode == 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                s_rgAvailable = false;
            }
            finally
            {
                s_probed = true;
            }

            return s_rgAvailable;
        }
        finally
        {
            s_probeGate.Release();
        }
    }

    /// <summary>内容搜索：rg 可用则走子进程，否则托管 GrepSearch。</summary>
    public static async Task<List<GrepMatch>> GrepAsync(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        string? includePattern,
        int limit,
        CancellationToken ct = default)
    {
        if (await IsRipgrepAvailableAsync(world.Subprocess, ct).ConfigureAwait(false))
        {
            try
            {
                var viaRg = await GrepViaRipgrepAsync(world.Subprocess, searchPath, pattern, includePattern, limit, ct)
                    .ConfigureAwait(false);
                if (viaRg is not null)
                    return viaRg;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // fall through to managed
            }
        }

        return FileSystemHelper.GrepSearch(world.FileSystem, searchPath, pattern, includePattern, limit);
    }

    /// <summary>Glob：rg --files 可用则走子进程，否则托管 GlobSearch。</summary>
    public static async Task<List<string>> GlobAsync(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        int limit,
        CancellationToken ct = default)
    {
        if (await IsRipgrepAvailableAsync(world.Subprocess, ct).ConfigureAwait(false))
        {
            try
            {
                var viaRg = await GlobViaRipgrepAsync(world.Subprocess, searchPath, pattern, limit, ct)
                    .ConfigureAwait(false);
                if (viaRg is not null)
                    return viaRg;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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
        s_probed = false;
        s_rgAvailable = false;
    }

    private static async Task<List<GrepMatch>?> GrepViaRipgrepAsync(
        ISubprocessFactory subprocess,
        string searchPath,
        string pattern,
        string? includePattern,
        int limit,
        CancellationToken ct)
    {
        var args = new StringBuilder();
        // --json 输出结构化事件（path/line_number/lines 为独立字段），
        // 避免文本格式下 Windows 盘符 "C:" 被误判为 path:line 分隔符导致匹配全灭。
        args.Append("--json ");
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

        var stdout = await ReadStdoutWithTimeoutAsync(proc, SearchTimeout, ct).ConfigureAwait(false);
        if (stdout is null)
            return null;

        // rg: 0=matches, 1=no matches, 2=error
        if (proc.ExitCode > 1)
            return null;

        var matches = new List<GrepMatch>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (matches.Count >= limit)
                break;

            if (TryParseMatchEvent(line, out var match))
                matches.Add(match);
        }

        return matches;
    }

    private static async Task<List<string>?> GlobViaRipgrepAsync(
        ISubprocessFactory subprocess,
        string searchPath,
        string pattern,
        int limit,
        CancellationToken ct)
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

        var stdout = await ReadStdoutWithTimeoutAsync(proc, SearchTimeout, ct).ConfigureAwait(false);
        if (stdout is null)
            return null;

        if (proc.ExitCode > 1)
            return null;

        return stdout
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// 读取子进程 stdout 并等待退出；内部施加超时（<paramref name="timeout"/>）。
    /// 超时返回 <see langword="null"/> 并终止进程（交由上层回退托管实现）；
    /// 外部 <paramref name="ct"/> 取消时原样抛出。
    /// </summary>
    private static async Task<string?> ReadStdoutWithTimeoutAsync(
        ISubprocess proc,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return stdout;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            return null;
        }
    }

    /// <summary>探测阶段同步等待（经异步入口 + 超时），失败时终止进程。</summary>
    private static async Task RunWithTimeoutAsync(ISubprocess proc, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            TryKill(proc);
            throw;
        }
    }

    private static void TryKill(ISubprocess proc)
    {
        try
        {
            proc.Kill();
        }
        catch
        {
            // 进程可能已退出
        }
    }

    /// <summary>解析 rg --json 的 match 事件行。</summary>
    private static bool TryParseMatchEvent(string line, out GrepMatch match)
    {
        match = null!;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "match")
            {
                return false;
            }

            if (!root.TryGetProperty("data", out var data))
                return false;

            if (!data.TryGetProperty("path", out var pathElement) ||
                !pathElement.TryGetProperty("text", out var pathText))
            {
                return false;
            }

            var path = pathText.GetString();
            if (string.IsNullOrEmpty(path))
                return false;

            var lineNumber = data.TryGetProperty("line_number", out var lineNumberElement)
                ? lineNumberElement.GetInt32()
                : 0;

            var text = string.Empty;
            if (data.TryGetProperty("lines", out var linesElement) &&
                linesElement.TryGetProperty("text", out var linesText))
            {
                text = linesText.GetString() ?? string.Empty;
            }

            match = new GrepMatch
            {
                Path = path,
                LineNum = lineNumber,
                // rg --json 的 lines.text 含行尾换行符，去除以与托管实现保持一致
                LineText = text.TrimEnd('\r', '\n'),
                ModTime = 0
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Quote(string value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
