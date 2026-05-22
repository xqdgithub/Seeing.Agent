using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Agent.Git
{
    /// <summary>
    /// Git 服务实现 - 通过 git CLI 执行操作
    /// </summary>
    public class GitService : IGitService
    {
        private readonly ILogger<GitService> _logger;
        private readonly GitOptions _options;

        public GitService(ILogger<GitService> logger, IOptions<GitOptions>? options = null)
        {
            _logger = logger;
            _options = options?.Value ?? new GitOptions();
        }

        public async Task<GitStatus> GetStatusAsync(string? path = null, CancellationToken cancellationToken = default)
        {
            var result = await ExecuteAsync("status", new[] { "--porcelain=v2", "--branch" }, cancellationToken);
            EnsureSuccess(result, "status");

            var status = new GitStatus();
            var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.StartsWith("# branch.head "))
                {
                    status.Branch = line[13..].Trim();
                }
                else if (line.StartsWith("# branch.ab "))
                {
                    var parts = line[12..].Trim().Split(' ');
                    if (parts.Length >= 2)
                    {
                        var ahead = int.Parse(parts[0].TrimStart('+'));
                        var behind = int.Parse(parts[1].TrimStart('-'));
                        if (ahead > 0) status.Ahead = $"+{ahead}";
                        if (behind > 0) status.Behind = $"-{behind}";
                    }
                }
                else if (line.Length >= 2)
                {
                    var fileStatus = ParseFileStatus(line);
                    if (fileStatus != null)
                        status.Files.Add(fileStatus);
                }
            }

            status.IsClean = status.Files.Count == 0;
            status.HasUntrackedFiles = status.Files.Any(f => f.State == GitFileState.Untracked);
            status.HasStagedChanges = status.Files.Any(f => f.IsStaged);
            status.HasUnstagedChanges = status.Files.Any(f => !f.IsStaged && f.State != GitFileState.Untracked);

            return status;
        }

        public async Task<GitDiff> GetDiffAsync(string? path = null, bool staged = false, CancellationToken cancellationToken = default)
        {
            var args = new List<string> { "--no-color" };
            if (staged) args.Add("--staged");
            if (path != null) args.Add("--");
            if (path != null) args.Add(path);

            var result = await ExecuteAsync("diff", args, cancellationToken);
            EnsureSuccess(result, "diff");

            return ParseDiff(result.StdOut);
        }

        public async Task<IReadOnlyList<GitCommit>> GetLogAsync(
            string? path = null,
            int maxCount = 50,
            string? since = null,
            string? until = null,
            CancellationToken cancellationToken = default)
        {
            var args = new List<string>
            {
                $"--format=%H%n%s%n%an%n%ae%n%at%n%P%n---",
                $"-n", maxCount.ToString()
            };

            if (since != null) args.Add($"--since={since}");
            if (until != null) args.Add($"--until={until}");
            if (path != null)
            {
                args.Add("--");
                args.Add(path);
            }

            var result = await ExecuteAsync("log", args, cancellationToken);
            EnsureSuccess(result, "log");

            return ParseCommits(result.StdOut);
        }

        public async Task<GitResult> AddAsync(IEnumerable<string>? paths = null, bool all = false, CancellationToken cancellationToken = default)
        {
            var args = new List<string>();
            if (all) args.Add("-A");
            if (paths != null)
            {
                args.Add("--");
                args.AddRange(paths);
            }

            return await ExecuteAsync("add", args, cancellationToken);
        }

        public async Task<GitResult> CommitAsync(string message, bool allowEmpty = false, string? author = null, CancellationToken cancellationToken = default)
        {
            var args = new List<string> { "-m", message };
            if (allowEmpty) args.Add("--allow-empty");
            if (author != null) args.AddRange(new[] { "--author", author });

            return await ExecuteAsync("commit", args, cancellationToken);
        }

        public async Task<GitResult> PushAsync(string? remote = null, string? branch = null, bool force = false, CancellationToken cancellationToken = default)
        {
            var args = new List<string>();
            if (force) args.Add("--force");
            if (remote != null) args.Add(remote);
            if (branch != null) args.Add(branch);

            return await ExecuteAsync("push", args, cancellationToken);
        }

        public async Task<GitResult> PullAsync(string? remote = null, string? branch = null, CancellationToken cancellationToken = default)
        {
            var args = new List<string>();
            if (remote != null) args.Add(remote);
            if (branch != null) args.Add(branch);

            return await ExecuteAsync("pull", args, cancellationToken);
        }

        public async Task<GitResult> CheckoutAsync(string branch, bool createNew = false, CancellationToken cancellationToken = default)
        {
            var args = new List<string>();
            if (createNew) args.Add("-b");
            args.Add(branch);

            return await ExecuteAsync("checkout", args, cancellationToken);
        }

        public async Task<GitResult> CreateBranchAsync(string branch, string? startPoint = null, CancellationToken cancellationToken = default)
        {
            var args = new List<string> { branch };
            if (startPoint != null) args.Add(startPoint);

            return await ExecuteAsync("branch", args, cancellationToken);
        }

        public async Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
        {
            var result = await ExecuteAsync("rev-parse", new[] { "--abbrev-ref", "HEAD" }, cancellationToken);
            return result.Success ? result.StdOut.Trim() : "HEAD";
        }

        public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default)
        {
            var result = await ExecuteAsync("branch", new[] { "-vv", "--no-color" }, cancellationToken);
            if (!result.Success) return new List<GitBranch>();

            var branches = new List<GitBranch>();
            var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var branch = ParseBranch(line);
                if (branch != null)
                    branches.Add(branch);
            }

            return branches;
        }

        public async Task<GitResult> ExecuteAsync(string command, IEnumerable<string>? args = null, CancellationToken cancellationToken = default)
        {
            var fullArgs = new List<string> { command };
            if (args != null) fullArgs.AddRange(args);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _options.GitPath,
                    Arguments = string.Join(" ", fullArgs.Select(EscapeArg)),
                    WorkingDirectory = _options.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.Timeout);

                await process.WaitForExitAsync(cts.Token);

                return new GitResult
                {
                    Success = process.ExitCode == 0,
                    ExitCode = process.ExitCode,
                    StdOut = outputBuilder.ToString(),
                    StdErr = errorBuilder.ToString()
                };
            }
            catch (OperationCanceledException)
            {
                return new GitResult
                {
                    Success = false,
                    ExitCode = -1,
                    Error = "Operation timed out"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git command failed: {Command}", command);
                return new GitResult
                {
                    Success = false,
                    ExitCode = -1,
                    Error = ex.Message
                };
            }
        }

        public async Task<bool> IsInRepositoryAsync(string? path = null, CancellationToken cancellationToken = default)
        {
            var args = new List<string> { "rev-parse", "--is-inside-work-tree" };
            var result = await ExecuteAsync("rev-parse", new[] { "--is-inside-work-tree" }, cancellationToken);
            return result.Success && result.StdOut.Trim() == "true";
        }

        private void EnsureSuccess(GitResult result, string command)
        {
            if (!result.Success)
                throw GitException.FromResult(result, command);
        }

        private static string EscapeArg(string arg)
        {
            if (arg.Contains(' ') || arg.Contains('"') || arg.Contains('\\'))
                return $"\"{arg.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
            return arg;
        }

        private GitFileStatus? ParseFileStatus(string line)
        {
            if (line.Length < 2) return null;

            var state = GitFileState.Unmodified;
            var isStaged = false;

            if (line[0] == '1') // Ordinary entry
            {
                var xy = line.Substring(2, 2);
                isStaged = xy[0] != '.' && xy[0] != '?';
                state = xy[0] switch
                {
                    'A' => GitFileState.Added,
                    'M' => GitFileState.Modified,
                    'D' => GitFileState.Deleted,
                    'R' => GitFileState.Renamed,
                    'C' => GitFileState.Copied,
                    '?' => GitFileState.Untracked,
                    '!' => GitFileState.Ignored,
                    _ => xy[1] switch
                    {
                        'M' => GitFileState.Modified,
                        'D' => GitFileState.Deleted,
                        _ => GitFileState.Unmodified
                    }
                };

                // Extract path (last field after spaces)
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = parts.Length > 8 ? parts[^1] : "";
                return new GitFileStatus { Path = path, State = state, IsStaged = isStaged };
            }
            else if (line[0] == '2') // Renamed/copied entry
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 9)
                {
                    return new GitFileStatus
                    {
                        Path = parts[^1],
                        State = line[2] == 'R' ? GitFileState.Renamed : GitFileState.Copied,
                        IsStaged = true
                    };
                }
            }
            else if (line[0] == '?') // Untracked
            {
                return new GitFileStatus
                {
                    Path = line[2..].Trim(),
                    State = GitFileState.Untracked,
                    IsStaged = false
                };
            }

            return null;
        }

        private GitDiff ParseDiff(string rawDiff)
        {
            var diff = new GitDiff { RawDiff = rawDiff };
            // Simplified parsing - real implementation would be more thorough
            var filePattern = new Regex(@"^diff --git a/(.+) b/(.+)$", RegexOptions.Multiline);
            var matches = filePattern.Matches(rawDiff);

            foreach (Match match in matches)
            {
                diff.Files.Add(new GitDiffFile
                {
                    Path = match.Groups[2].Value,
                    OldPath = match.Groups[1].Value
                });
            }

            // Count +/- lines
            foreach (var line in rawDiff.Split('\n'))
            {
                if (line.StartsWith('+') && !line.StartsWith("+++")) diff.TotalAddedLines++;
                else if (line.StartsWith('-') && !line.StartsWith("---")) diff.TotalDeletedLines++;
            }

            return diff;
        }

        private List<GitCommit> ParseCommits(string log)
        {
            var commits = new List<GitCommit>();
            var blocks = log.Split("---\n", StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= 5)
                {
                    commits.Add(new GitCommit
                    {
                        Hash = lines[0],
                        Message = lines[1],
                        Author = lines[2],
                        AuthorEmail = lines[3],
                        Date = DateTimeOffset.FromUnixTimeSeconds(long.Parse(lines[4])),
                        Parents = lines.Length > 5 ? lines[5].Split(' ').ToList() : new List<string>()
                    });
                }
            }

            return commits;
        }

        private GitBranch? ParseBranch(string line)
        {
            var match = Regex.Match(line, @"^(\*?)\s+(.+?)(?:\s+(\S+)\s+\[([^\]]+)\])?");
            if (!match.Success) return null;

            var branch = new GitBranch
            {
                Name = match.Groups[2].Value,
                IsCurrent = match.Groups[1].Value == "*",
                Upstream = match.Groups[3].Success ? match.Groups[3].Value : null
            };

            if (match.Groups[4].Success)
            {
                var tracking = match.Groups[4].Value;
                var aheadMatch = Regex.Match(tracking, @"ahead (\d+)");
                var behindMatch = Regex.Match(tracking, @"behind (\d+)");
                if (aheadMatch.Success) branch.Ahead = int.Parse(aheadMatch.Groups[1].Value);
                if (behindMatch.Success) branch.Behind = int.Parse(behindMatch.Groups[1].Value);
            }

            return branch;
        }
    }
}
