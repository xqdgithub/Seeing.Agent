using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem;

/// <summary>
/// 优先经 <see cref="ISubprocess"/> 调用 ripgrep（<c>rg</c>）；不可用时回退托管枚举 + <see cref="IFileSystem"/>。
/// <para>
/// 契约要点（与托管后端对齐，见设计文档 §5）：
/// <list type="bullet">
/// <item>参数一律经 <see cref="SubprocessSpec.ArgumentList"/> 逐项传递，绝不拼接命令行字符串。</item>
/// <item>退出码三态：0=有匹配 / 1=无匹配（<b>可信，不回退</b>）/ 2=出错（回退托管）。</item>
/// <item>超时不回退：两次 30s 串行会突破工具 60s 预算。</item>
/// <item>隐藏文件、大小写、排序、全局截断语义与托管后端一致。</item>
/// </list>
/// </para>
/// </summary>
internal static class RipgrepSearch
{
    /// <summary>探测 rg 可用性的超时。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>单次搜索子进程的超时。</summary>
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>搜索子进程 stdout 保留上限（1MB）；超出后继续排空管道但不再累积，避免内存峰值无上限。</summary>
    private const int MaxSearchOutputChars = 1024 * 1024;

    /// <summary>stderr 诊断信息保留上限（64KB）。</summary>
    private const int MaxStderrChars = 64 * 1024;

    /// <summary>诊断摘要（回灌给模型）的最大字符数。</summary>
    private const int MaxDiagnosticChars = 200;

    private static readonly SemaphoreSlim s_probeGate = new(1, 1);
    private static volatile bool s_probed;
    private static volatile bool s_rgAvailable;

    /// <summary>回退托管的原因（供工具拼装诊断），null 表示未回退。</summary>
    internal sealed record Fallback(string Reason);

    /// <summary>搜索超时提示文案（与 <see cref="SearchTimeout"/> 一致）。</summary>
    internal const string TimeoutDisplay = "30s";

    /// <summary>搜索执行结果：区分"可信结果"与"回退发生"。</summary>
    internal sealed record SearchOutcome<T>(
        List<T> Items,
        string Backend,
        Fallback? Fallback,
        bool TimedOut)
    {
        public bool UsedManaged => Backend == "managed";
    }

    /// <summary>
    /// 探测当前执行世界是否能启动 <c>rg</c>（结果进程内缓存）。
    /// </summary>
    public static async Task<bool> IsRipgrepAvailableAsync(
        ISubprocessFactory subprocess,
        CancellationToken ct = default,
        ILogger? logger = null)
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
                    ArgumentList = ["--version"],
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
            catch (Exception ex)
            {
                // 探测失败只说明这台执行世界没有 rg，属正常降级；记一次便于排查
                logger?.LogWarning(ex, "ripgrep 不可用，glob/grep 将使用托管回退");
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

    /// <summary>内容搜索：rg 可用则走子进程，否则托管 <see cref="FileSystemHelper.GrepSearch"/>。</summary>
    public static async Task<SearchOutcome<GrepMatch>> GrepAsync(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        string? includePattern,
        int limit,
        CancellationToken ct = default,
        ILogger? logger = null,
        bool includeHidden = false)
    {
        var fallback = await TryRipgrepGrepAsync(world, searchPath, pattern, includePattern, limit, ct, logger, includeHidden)
            .ConfigureAwait(false);
        if (fallback.Outcome is not null)
            return fallback.Outcome;

        ct.ThrowIfCancellationRequested();
        var matches = FileSystemHelper.GrepSearch(
            world.FileSystem, searchPath, pattern, includePattern, limit, ct, includeHidden);
        return new SearchOutcome<GrepMatch>(matches, "managed", fallback.Reason, TimedOut: false);
    }

    /// <summary>Glob：rg <c>--files</c> 可用则走子进程，否则托管 <see cref="FileSystemHelper.GlobSearch"/>。</summary>
    public static async Task<SearchOutcome<string>> GlobAsync(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        int limit,
        CancellationToken ct = default,
        ILogger? logger = null,
        bool includeHidden = false)
    {
        var fallback = await TryRipgrepGlobAsync(world, searchPath, pattern, limit, ct, logger, includeHidden)
            .ConfigureAwait(false);
        if (fallback.Outcome is not null)
            return fallback.Outcome;

        ct.ThrowIfCancellationRequested();
        var files = FileSystemHelper.GlobSearch(world.FileSystem, searchPath, pattern, limit, ct, includeHidden);
        return new SearchOutcome<string>(files, "managed", fallback.Reason, TimedOut: false);
    }

    /// <summary>测试用：重置 rg 探测缓存。</summary>
    internal static void ResetProbeForTests()
    {
        s_probed = false;
        s_rgAvailable = false;
    }

    /// <summary>托管回退的包装：Outcome 非空表示 rg 已给出可信结果或超时，调用方不得回退。</summary>
    private readonly record struct Attempt<T>(SearchOutcome<T>? Outcome, Fallback? Reason);

    private static async Task<Attempt<GrepMatch>> TryRipgrepGrepAsync(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        string? includePattern,
        int limit,
        CancellationToken ct,
        ILogger? logger,
        bool includeHidden)
    {
        if (!await IsRipgrepAvailableAsync(world.Subprocess, ct, logger).ConfigureAwait(false))
            return new Attempt<GrepMatch>(null, new Fallback("ripgrep 未安装"));

        try
        {
            var result = await GrepViaRipgrepAsync(
                world.Subprocess, searchPath, pattern, includePattern, limit, ct, logger, includeHidden).ConfigureAwait(false);

            switch (result.Status)
            {
                case RgStatus.Matched:
                case RgStatus.NoMatch:
                    return new Attempt<GrepMatch>(
                        new SearchOutcome<GrepMatch>(result.Items!, "rg", null, TimedOut: false), null);

                case RgStatus.TimedOut:
                    // 超时不回退：托管遍历更慢，且会再吃 30s 预算
                    return new Attempt<GrepMatch>(
                        new SearchOutcome<GrepMatch>([], "rg", null, TimedOut: true), null);

                default:
                    return new Attempt<GrepMatch>(null, new Fallback(result.Diagnostic ?? "ripgrep 执行失败"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Attempt<GrepMatch>(null, new Fallback(Summarize(ex.Message)));
        }
    }

    private static async Task<Attempt<string>> TryRipgrepGlobAsync(
        IExecutionWorld world,
        string searchPath,
        string pattern,
        int limit,
        CancellationToken ct,
        ILogger? logger,
        bool includeHidden)
    {
        if (!await IsRipgrepAvailableAsync(world.Subprocess, ct, logger).ConfigureAwait(false))
            return new Attempt<string>(null, new Fallback("ripgrep 未安装"));

        try
        {
            var result = await GlobViaRipgrepAsync(world.Subprocess, searchPath, pattern, limit, ct, logger, includeHidden)
                .ConfigureAwait(false);

            switch (result.Status)
            {
                case RgStatus.Matched:
                case RgStatus.NoMatch:
                    return new Attempt<string>(
                        new SearchOutcome<string>(result.Items!, "rg", null, TimedOut: false), null);

                case RgStatus.TimedOut:
                    return new Attempt<string>(
                        new SearchOutcome<string>([], "rg", null, TimedOut: true), null);

                default:
                    return new Attempt<string>(null, new Fallback(result.Diagnostic ?? "ripgrep 执行失败"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Attempt<string>(null, new Fallback(Summarize(ex.Message)));
        }
    }

    private enum RgStatus
    {
        /// <summary>退出码 0：有匹配（或无匹配但命令成功——对 --files 而言 0 就是有结果）。</summary>
        Matched,
        /// <summary>退出码 1：无匹配（可信答案）。</summary>
        NoMatch,
        /// <summary>退出码 2 或解析失败：不可信，应回退。</summary>
        Error,
        /// <summary>超时：不回退。</summary>
        TimedOut,
    }

    private readonly record struct RgResult<T>(RgStatus Status, List<T>? Items, string? Diagnostic);

    private static async Task<RgResult<GrepMatch>> GrepViaRipgrepAsync(
        ISubprocessFactory subprocess,
        string searchPath,
        string pattern,
        string? includePattern,
        int limit,
        CancellationToken ct,
        ILogger? logger,
        bool includeHidden)
    {
        var args = new List<string>
        {
            // --json 输出结构化事件（path/line_number/lines 为独立字段），
            // 避免文本格式下 Windows 盘符 "C:" 被误判为 path:line 分隔符导致匹配全灭。
            "--json",
            // --max-count 是「每文件」上限，不是全局上限（实测 3 文件 × 5 行 = 15 条）。
            // 全局截断在 C# 侧按累计条数判定。
            "--max-count", Math.Max(1, limit).ToString(),
        };

        if (includeHidden)
            args.Add("--hidden");

        if (FileSystemHelper.IsCaseInsensitivePathComparison())
            args.Add("--glob-case-insensitive");

        if (!string.IsNullOrWhiteSpace(includePattern))
        {
            args.Add("-g");
            args.Add(GlobMatcher.NormalizePattern(includePattern));
        }

        // -- 之后一律按字面量传入：pattern 与 path 都不需要转义
        args.Add("--");
        args.Add(pattern);
        args.Add(searchPath);

        using var proc = subprocess.Start(new SubprocessSpec
        {
            FileName = "rg",
            ArgumentList = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        var (stdout, stderr) = await ReadStdoutWithTimeoutAsync(proc, SearchTimeout, ct).ConfigureAwait(false);

        if (stdout is null)
        {
            LogStderr(logger, "grep --json", stderr);
            return new RgResult<GrepMatch>(RgStatus.TimedOut, null, null);
        }

        // rg: 0=matches, 1=no matches, 2=error
        if (proc.ExitCode > 1)
        {
            LogStderr(logger, "grep --json", stderr);
            return new RgResult<GrepMatch>(RgStatus.Error, null, Summarize(stderr));
        }

        var matches = new List<GrepMatch>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 多取一条用于判定截断，最终由工具层 Take(limit)
            if (matches.Count > limit)
                break;

            if (TryParseMatchEvent(line, out var match))
                matches.Add(match);
        }

        var status = proc.ExitCode == 1 ? RgStatus.NoMatch : RgStatus.Matched;
        return new RgResult<GrepMatch>(status, matches, null);
    }

    private static async Task<RgResult<string>> GlobViaRipgrepAsync(
        ISubprocessFactory subprocess,
        string searchPath,
        string pattern,
        int limit,
        CancellationToken ct,
        ILogger? logger,
        bool includeHidden)
    {
        var args = new List<string>
        {
            "--files",
            "--color", "never",
            // 与托管后端和工具描述一致：按修改时间倒序
            "--sortr=modified",
        };

        if (includeHidden)
            args.Add("--hidden");

        if (FileSystemHelper.IsCaseInsensitivePathComparison())
            args.Add("--glob-case-insensitive");

        // 注意：--files 不接受 --max-count（实测传了被忽略），全局截断只能在 C# 侧做
        args.Add("-g");
        args.Add(GlobMatcher.NormalizePattern(pattern));
        args.Add(searchPath);

        using var proc = subprocess.Start(new SubprocessSpec
        {
            FileName = "rg",
            ArgumentList = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        var (stdout, stderr) = await ReadStdoutWithTimeoutAsync(proc, SearchTimeout, ct).ConfigureAwait(false);

        if (stdout is null)
        {
            LogStderr(logger, "glob --files", stderr);
            return new RgResult<string>(RgStatus.TimedOut, null, null);
        }

        if (proc.ExitCode > 1)
        {
            LogStderr(logger, "glob --files", stderr);
            return new RgResult<string>(RgStatus.Error, null, Summarize(stderr));
        }

        // 多保留一条用于判定截断
        var files = stdout
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(limit + 1)
            .ToList();

        var status = proc.ExitCode == 1 || files.Count == 0 ? RgStatus.NoMatch : RgStatus.Matched;
        return new RgResult<string>(status, files, null);
    }

    /// <summary>
    /// 并发读取子进程 stdout 与 stderr（必须同时消费两个管道，否则任一方填满都会使子进程阻塞、
    /// <see cref="ISubprocess.WaitForExitAsync"/> 死锁），并等待退出；内部施加超时（<paramref name="timeout"/>）。
    /// 超时返回 <c>(null, null)</c> 并终止进程（交由上层回退托管实现）；
    /// 外部 <paramref name="ct"/> 取消时原样抛出。<paramref name="stderr"/> 供失败诊断。
    /// </summary>
    private static async Task<(string? Stdout, string? Stderr)> ReadStdoutWithTimeoutAsync(
        ISubprocess proc,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdoutTask = ReadTextLimitedAsync(proc.StandardOutput, MaxSearchOutputChars, cts.Token);
        var stderrTask = ReadTextLimitedAsync(proc.StandardError, MaxStderrChars, cts.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户取消：必须终止子进程，否则 rg 会继续扫盘占用 IO/CPU
            TryKill(proc);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            return (null, null);
        }
    }

    /// <summary>
    /// 流式读取文本：累积至 <paramref name="maxChars"/> 后继续读取但不再累积，
    /// 既排空管道（防子进程阻塞），又避免 <c>ReadToEndAsync</c> 在超大输出下的无上限内存峰值。
    /// </summary>
    private static async Task<string> ReadTextLimitedAsync(TextReader reader, int maxChars, CancellationToken ct)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 8192));
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            var remaining = maxChars - builder.Length;
            if (remaining > 0)
                builder.Append(buffer, 0, Math.Min(remaining, read));
        }

        return builder.ToString();
    }

    /// <summary>失败时记录 rg stderr 以便诊断（内容为空则跳过）。</summary>
    private static void LogStderr(ILogger? logger, string operation, string? stderr)
    {
        if (logger is null || string.IsNullOrWhiteSpace(stderr))
            return;

        logger.LogWarning("ripgrep {Operation} 失败，stderr: {Stderr}", operation, stderr);
    }

    /// <summary>把 stderr / 异常消息压成单行短摘要，用于回灌给模型。</summary>
    internal static string Summarize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "ripgrep 执行失败";

        var line = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? message;

        return line.Length > MaxDiagnosticChars
            ? line[..MaxDiagnosticChars] + "…"
            : line;
    }

    /// <summary>探测阶段同步等待（经异步入口 + 超时），失败时终止进程。</summary>
    private static async Task RunWithTimeoutAsync(ISubprocess proc, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            // 并发排空 stdout/stderr，避免任一管道填满导致探测死锁。
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
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

            if (!data.TryGetProperty("path", out var pathElement))
                return false;

            var path = ReadRgText(pathElement);
            if (string.IsNullOrEmpty(path))
                return false;

            var lineNumber = data.TryGetProperty("line_number", out var lineNumberElement)
                ? lineNumberElement.GetInt32()
                : 0;

            var text = string.Empty;
            if (data.TryGetProperty("lines", out var linesElement))
                text = ReadRgText(linesElement) ?? string.Empty;

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

    /// <summary>
    /// 读取 rg --json 的文本字段：优先 <c>text</c>，非 UTF-8 时退回 <c>bytes</c>（base64）。
    /// <para>没有 <c>bytes</c> 分支会静默丢弃非 UTF-8 文件名（Linux 上合法）。</para>
    /// </summary>
    private static string? ReadRgText(JsonElement element)
    {
        if (element.TryGetProperty("text", out var textElement))
            return textElement.GetString();

        if (element.TryGetProperty("bytes", out var bytesElement))
        {
            var base64 = bytesElement.GetString();
            if (string.IsNullOrEmpty(base64))
                return null;

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }
}
