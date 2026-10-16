using System.Text;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.Workspace;

/// <summary>
/// 将工作区的 <c>.seeing</c> 目录幂等加入所属 git 仓库的本地排除文件
/// <c>.git/info/exclude</c>（不修改 <c>.gitignore</c>，不产生被追踪变更）。
/// <para>
/// 决策链：非仓库 / 已忽略（含 .gitignore）→ 不写；否则追加带标记的块。
/// 所有失败均降级为结果枚举，不向调用方抛出。
/// </para>
/// </summary>
public sealed class WorkspaceGitExcluder
{
    /// <summary>托管块起始标记。</summary>
    public const string MarkerBegin = "# >>> Seeing.Agent managed ignore >>>";

    /// <summary>托管块结束标记。</summary>
    public const string MarkerEnd = "# <<< Seeing.Agent managed ignore <<<";

    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ISubprocessFactory _subprocess;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<WorkspaceGitExcluder> _logger;
    private readonly TimeSpan _timeout;

    /// <summary>创建工作区 git 排除器。</summary>
    public WorkspaceGitExcluder(
        ISubprocessFactory subprocess,
        IFileSystem fileSystem,
        ILogger<WorkspaceGitExcluder> logger,
        TimeSpan? timeout = null)
    {
        _subprocess = subprocess ?? throw new ArgumentNullException(nameof(subprocess));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout ?? s_defaultTimeout;
    }

    /// <summary>
    /// 确保 <paramref name="workspaceRoot"/> 下的 <c>.seeing</c> 被其 git 仓库本地排除。
    /// </summary>
    public async Task<GitExcludeOutcome> EnsureExcludedAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return GitExcludeOutcome.NotARepository;

        string seeingDir;
        try
        {
            seeingDir = Path.Combine(workspaceRoot, ".seeing");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "无法解析工作区 .seeing 路径: {Workspace}", workspaceRoot);
            return GitExcludeOutcome.Failed;
        }

        // 1) 定位仓库根
        var top = await RunGitAsync(workspaceRoot, ["rev-parse", "--show-toplevel"], cancellationToken)
            .ConfigureAwait(false);
        if (top.Unavailable)
            return GitExcludeOutcome.Unavailable;
        if (!top.Success)
            return GitExcludeOutcome.NotARepository;

        var repoRoot = top.StdOut.Trim();
        if (string.IsNullOrEmpty(repoRoot))
            return GitExcludeOutcome.NotARepository;

        // 2) 计算锚定模式
        string relative;
        try
        {
            relative = Path.GetRelativePath(repoRoot, seeingDir).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "无法计算 .seeing 相对仓库根路径: {Seeing} / {Repo}", seeingDir, repoRoot);
            return GitExcludeOutcome.Failed;
        }

        if (relative is "." || relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return GitExcludeOutcome.OutsideRepository;
        }

        var pattern = "/" + relative.Trim('/') + "/";

        // 3) 已被任意来源忽略（.gitignore / info/exclude / 全局 excludes）→ 不写
        var probePath = seeingDir.Replace('\\', '/').TrimEnd('/') + "/";
        var probe = await RunGitAsync(repoRoot, ["check-ignore", "-q", "--no-index", "--", probePath], cancellationToken)
            .ConfigureAwait(false);
        if (probe.Unavailable)
            return GitExcludeOutcome.Unavailable;
        if (probe.Success)
            return GitExcludeOutcome.AlreadyIgnored;

        // 4) 定位 info/exclude
        var excludeFile = await ResolveExcludeFileAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        if (excludeFile is null)
            return GitExcludeOutcome.Failed;

        // 5) 幂等：托管块已存在 → 跳过
        string existing;
        try
        {
            existing = _fileSystem.Exists(excludeFile)
                ? await _fileSystem.ReadAllTextAsync(excludeFile, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 git 排除文件失败: {Path}", excludeFile);
            return GitExcludeOutcome.Failed;
        }

        if (existing.Contains(MarkerBegin, StringComparison.Ordinal))
            return GitExcludeOutcome.AlreadyManaged;

        // 6) 追加托管块（保留既有内容与换行风格）
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = new StringBuilder();
        if (existing.Length > 0 && !existing.EndsWith('\n'))
            block.Append(newline);
        block.Append(MarkerBegin).Append(newline);
        block.Append(pattern).Append(newline);
        block.Append(MarkerEnd).Append(newline);

        try
        {
            await _fileSystem.WriteAllTextAsync(
                excludeFile,
                existing + block.ToString(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 git 排除文件失败: {Path}", excludeFile);
            return GitExcludeOutcome.Failed;
        }

        _logger.LogInformation("已将 {Pattern} 加入 git 本地排除: {Path}", pattern, excludeFile);
        return GitExcludeOutcome.Applied;
    }

    private async Task<string?> ResolveExcludeFileAsync(string repoRoot, CancellationToken cancellationToken)
    {
        // 优先请求绝对路径（git >= 2.31）；旧版本回退到相对路径 + repoRoot 拼接。
        var result = await RunGitAsync(
            repoRoot,
            ["rev-parse", "--path-format=absolute", "--git-path", "info/exclude"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            result = await RunGitAsync(
                repoRoot,
                ["rev-parse", "--git-path", "info/exclude"],
                cancellationToken).ConfigureAwait(false);
        }

        if (!result.Success)
            return null;

        var path = result.StdOut.Trim();
        if (string.IsNullOrEmpty(path))
            return null;

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(repoRoot, path));
    }

    private async Task<GitRunResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var spec = new SubprocessSpec
        {
            FileName = "git",
            Arguments = string.Join(' ', args.Select(Quote)),
            WorkingDirectory = workingDirectory,
            CancellationToken = cancellationToken,
        };

        try
        {
            using var process = _subprocess.Start(spec);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            var token = timeoutCts.Token;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return GitRunResult.TimedOut();
            }

            return new GitRunResult(
                Success: process.ExitCode == 0,
                Unavailable: false,
                StdOut: await stdoutTask.ConfigureAwait(false),
                StdErr: await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // git 未安装 / 无法启动
            _logger.LogDebug(ex, "git 不可用，跳过 .seeing 自动排除");
            return GitRunResult.UnavailableResult();
        }
    }

    private void TryKill(ISubprocess process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "终止 git 进程失败");
        }
    }

    /// <summary>按 MSVCRT 规则为命令行参数加引号。</summary>
    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;

        var sb = new StringBuilder();
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    break;
                case '"':
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    break;
                default:
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private readonly record struct GitRunResult(bool Success, bool Unavailable, string StdOut, string StdErr)
    {
        public static GitRunResult UnavailableResult() => new(false, true, string.Empty, string.Empty);

        public static GitRunResult TimedOut() => new(false, false, string.Empty, "timeout");
    }
}
