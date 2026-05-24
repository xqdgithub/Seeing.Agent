namespace Seeing.Agent.Skills.Pulling
{
    /// <summary>
    /// Skill 拉取器接口 - 从远程源拉取 Skill
    /// </summary>
    public interface ISkillPuller
    {
        /// <summary>从 Git 仓库拉取 Skill</summary>
        Task<SkillPullResult> PullFromGitAsync(
            string repoUrl,
            string? branch = null,
            string? path = null,
            CancellationToken cancellationToken = default);

        /// <summary>从 HTTP URL 拉取 Skill</summary>
        Task<SkillPullResult> PullFromHttpAsync(
            string url,
            CancellationToken cancellationToken = default);

        /// <summary>从本地路径拉取 Skill</summary>
        Task<SkillPullResult> PullFromLocalAsync(
            string path,
            CancellationToken cancellationToken = default);

        /// <summary>验证 Skill 源是否有效</summary>
        Task<SkillValidationResult> ValidateSourceAsync(
            string source,
            CancellationToken cancellationToken = default);

        /// <summary>列出远程仓库中的可用 Skills</summary>
        Task<IReadOnlyList<SkillPullInfo>> ListRemoteSkillsAsync(
            string repoUrl,
            string? branch = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Skill 拉取结果</summary>
    public class SkillPullResult
    {
        public bool Success { get; set; }
        public string? SkillId { get; set; }
        public string? SkillName { get; set; }
        public string? LocalPath { get; set; }
        public string? SourceUrl { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>Skill 验证结果</summary>
    public class SkillValidationResult
    {
        public bool IsValid { get; set; }
        public string? Error { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>远程 Skill 信息</summary>
    public class SkillPullInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Version { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }
}
