namespace Seeing.Agent.Git
{
    /// <summary>
    /// Git 服务接口 - 封装 git CLI 操作
    /// </summary>
    public interface IGitService
    {
        /// <summary>获取仓库状态</summary>
        Task<GitStatus> GetStatusAsync(string? path = null, CancellationToken cancellationToken = default);

        /// <summary>获取差异</summary>
        Task<GitDiff> GetDiffAsync(string? path = null, bool staged = false, CancellationToken cancellationToken = default);

        /// <summary>获取提交历史</summary>
        Task<IReadOnlyList<GitCommit>> GetLogAsync(
            string? path = null,
            int maxCount = 50,
            string? since = null,
            string? until = null,
            CancellationToken cancellationToken = default);

        /// <summary>暂存文件</summary>
        Task<GitResult> AddAsync(
            IEnumerable<string>? paths = null,
            bool all = false,
            CancellationToken cancellationToken = default);

        /// <summary>提交更改</summary>
        Task<GitResult> CommitAsync(
            string message,
            bool allowEmpty = false,
            string? author = null,
            CancellationToken cancellationToken = default);

        /// <summary>推送到远程</summary>
        Task<GitResult> PushAsync(
            string? remote = null,
            string? branch = null,
            bool force = false,
            CancellationToken cancellationToken = default);

        /// <summary>从远程拉取</summary>
        Task<GitResult> PullAsync(
            string? remote = null,
            string? branch = null,
            CancellationToken cancellationToken = default);

        /// <summary>切换分支</summary>
        Task<GitResult> CheckoutAsync(
            string branch,
            bool createNew = false,
            CancellationToken cancellationToken = default);

        /// <summary>创建分支</summary>
        Task<GitResult> CreateBranchAsync(
            string branch,
            string? startPoint = null,
            CancellationToken cancellationToken = default);

        /// <summary>获取当前分支名</summary>
        Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default);

        /// <summary>获取所有分支</summary>
        Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default);

        /// <summary>执行任意 git 命令</summary>
        Task<GitResult> ExecuteAsync(
            string command,
            IEnumerable<string>? args = null,
            CancellationToken cancellationToken = default);

        /// <summary>检查是否在 Git 仓库中</summary>
        Task<bool> IsInRepositoryAsync(string? path = null, CancellationToken cancellationToken = default);
    }

    /// <summary>Git 操作结果</summary>
    public class GitResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = string.Empty;
        public string StdErr { get; set; } = string.Empty;
        public string? Error { get; set; }
    }
}
