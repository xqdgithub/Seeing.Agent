using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Skills.Pulling
{
    /// <summary>
    /// Skill 拉取器实现 - 支持 Git、HTTP、本地路径
    /// </summary>
    public class SkillPuller : ISkillPuller
    {
        private readonly ILogger<SkillPuller> _logger;
        private readonly string _skillStoragePath;

        public SkillPuller(ILogger<SkillPuller> logger, string? skillStoragePath = null)
        {
            _logger = logger;
            _skillStoragePath = skillStoragePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "skills");
        }

        public async Task<SkillPullResult> PullFromGitAsync(
            string repoUrl,
            string? branch = null,
            string? path = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // 解析仓库 URL
                var (owner, repo, subPath) = ParseGitHubUrl(repoUrl);
                if (owner == null)
                {
                    return new SkillPullResult
                    {
                        Success = false,
                        Error = $"Invalid Git URL: {repoUrl}"
                    };
                }

                // 构建 raw 文件 URL
                var skillPath = path ?? subPath ?? "skill.md";
                var branchRef = branch ?? "main";
                var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branchRef}/{skillPath}";

                _logger.LogInformation("Pulling skill from Git: {Url}", rawUrl);

                // 使用 HTTP 拉取
                var result = await PullFromHttpAsync(rawUrl, cancellationToken);
                result.SourceUrl = repoUrl;
                result.Metadata["source"] = "git";
                result.Metadata["repo"] = $"{owner}/{repo}";
                result.Metadata["branch"] = branchRef;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull skill from Git: {RepoUrl}", repoUrl);
                return new SkillPullResult
                {
                    Success = false,
                    SourceUrl = repoUrl,
                    Error = ex.Message
                };
            }
        }

        public async Task<SkillPullResult> PullFromHttpAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var content = await httpClient.GetStringAsync(url, cancellationToken);

                // 解析 Skill 名称
                var skillName = ExtractSkillName(content) ?? 
                    Path.GetFileNameWithoutExtension(new Uri(url).Segments.Last());

                // 保存到本地
                var localPath = await SaveSkillAsync(skillName, content, cancellationToken);

                _logger.LogInformation("Pulled skill {SkillName} from HTTP: {Url}", skillName, url);

                return new SkillPullResult
                {
                    Success = true,
                    SkillName = skillName,
                    LocalPath = localPath,
                    SourceUrl = url,
                    Metadata = new Dictionary<string, string>
                    {
                        ["source"] = "http",
                        ["pulled_at"] = DateTimeOffset.UtcNow.ToString("O")
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull skill from HTTP: {Url}", url);
                return new SkillPullResult
                {
                    Success = false,
                    SourceUrl = url,
                    Error = ex.Message
                };
            }
        }

        public async Task<SkillPullResult> PullFromLocalAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new SkillPullResult
                    {
                        Success = false,
                        Error = $"File not found: {path}"
                    };
                }

                var content = await File.ReadAllTextAsync(path, cancellationToken);
                var skillName = ExtractSkillName(content) ?? Path.GetFileNameWithoutExtension(path);

                // 复制到 skill 存储路径
                var targetPath = Path.Combine(_skillStoragePath, $"{skillName}.md");
                Directory.CreateDirectory(_skillStoragePath);
                await File.WriteAllTextAsync(targetPath, content, cancellationToken);

                _logger.LogInformation("Pulled skill {SkillName} from local: {Path}", skillName, path);

                return new SkillPullResult
                {
                    Success = true,
                    SkillName = skillName,
                    LocalPath = targetPath,
                    Metadata = new Dictionary<string, string>
                    {
                        ["source"] = "local",
                        ["original_path"] = path
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull skill from local: {Path}", path);
                return new SkillPullResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        public async Task<SkillValidationResult> ValidateSourceAsync(
            string source,
            CancellationToken cancellationToken = default)
        {
            // Git URL
            if (source.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://github.com", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://gitlab.com", StringComparison.OrdinalIgnoreCase))
            {
                var (owner, repo, _) = ParseGitHubUrl(source);
                if (owner != null)
                {
                    return new SkillValidationResult { IsValid = true };
                }
                return new SkillValidationResult
                {
                    IsValid = false,
                    Error = "Invalid Git URL format"
                };
            }

            // HTTP URL
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var httpClient = new System.Net.Http.HttpClient();
                    var response = await httpClient.SendAsync(
                        new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, source),
                        cancellationToken);
                    
                    return new SkillValidationResult
                    {
                        IsValid = response.IsSuccessStatusCode,
                        Error = response.IsSuccessStatusCode ? null : $"HTTP {response.StatusCode}"
                    };
                }
                catch (Exception ex)
                {
                    return new SkillValidationResult
                    {
                        IsValid = false,
                        Error = ex.Message
                    };
                }
            }

            // 本地路径
            if (File.Exists(source))
            {
                return new SkillValidationResult { IsValid = true };
            }

            return new SkillValidationResult
            {
                IsValid = false,
                Error = "Source is not a valid Git URL, HTTP URL, or local file path"
            };
        }

        public async Task<IReadOnlyList<SkillPullInfo>> ListRemoteSkillsAsync(
            string repoUrl,
            string? branch = null,
            CancellationToken cancellationToken = default)
        {
            var (owner, repo, _) = ParseGitHubUrl(repoUrl);
            if (owner == null)
            {
                return new List<SkillPullInfo>();
            }

            try
            {
                // 使用 GitHub API 列出仓库内容
                var branchRef = branch ?? "main";
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Seeing.Agent");

                var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/contents/skills?ref={branchRef}";
                var response = await httpClient.GetStringAsync(apiUrl, cancellationToken);

                // 简单解析 JSON（避免依赖）
                var skills = new List<SkillPullInfo>();
                var namePattern = new Regex(@"""name""\s*:\s*""([^""]+)""");
                var pathPattern = new Regex(@"""path""\s*:\s*""([^""]+)""");
                
                var matches = namePattern.Matches(response);
                var pathMatches = pathPattern.Matches(response);
                
                for (int i = 0; i < matches.Count && i < pathMatches.Count; i++)
                {
                    var name = matches[i].Groups[1].Value;
                    var path = pathMatches[i].Groups[1].Value;
                    
                    if (name.EndsWith(".md"))
                    {
                        skills.Add(new SkillPullInfo
                        {
                            Name = Path.GetFileNameWithoutExtension(name),
                            Path = path,
                            Description = $"Skill from {owner}/{repo}"
                        });
                    }
                }

                return skills;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list remote skills: {RepoUrl}", repoUrl);
                return new List<SkillPullInfo>();
            }
        }

        private (string? owner, string? repo, string? path) ParseGitHubUrl(string url)
        {
            // https://github.com/owner/repo
            // https://github.com/owner/repo/tree/branch/path
            // https://github.com/owner/repo/blob/branch/path/file.md
            var pattern = new Regex(@"github\.com/([^/]+)/([^/]+)(?:/(?:tree|blob)/[^/]+/(.+))?");
            var match = pattern.Match(url);
            
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Success ? match.Groups[3].Value : null);
            }
            return (null, null, null);
        }

        private string? ExtractSkillName(string content)
        {
            // 从 frontmatter 或第一个标题提取名称
            var lines = content.Split('\n');
            foreach (var line in lines.Take(20))
            {
                if (line.StartsWith("# "))
                {
                    return line[2..].Trim();
                }
            }
            return null;
        }

        private async Task<string> SaveSkillAsync(string skillName, string content, CancellationToken ct)
        {
            Directory.CreateDirectory(_skillStoragePath);
            var path = Path.Combine(_skillStoragePath, $"{skillName}.md");
            await File.WriteAllTextAsync(path, content, ct);
            return path;
        }
    }
}
