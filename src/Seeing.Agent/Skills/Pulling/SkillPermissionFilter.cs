using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Permission;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Skills
{
    /// <summary>
    /// Skill 权限过滤器 - 根据 Rules 过滤可用的 Skills
    /// </summary>
    public class SkillPermissionFilter
    {
        private readonly ILogger<SkillPermissionFilter> _logger;
        private readonly IPermissionService? _permissionService;

        public SkillPermissionFilter(ILogger<SkillPermissionFilter> logger, IPermissionService? permissionService = null)
        {
            _logger = logger;
            _permissionService = permissionService;
        }

        /// <summary>过滤 Skills 列表，返回允许访问的 Skills</summary>
        public async Task<IReadOnlyList<SkillAccess>> FilterAsync(
            IEnumerable<string> skillNames,
            PermissionContext? permissionContext = null,
            CancellationToken cancellationToken = default)
        {
            var result = new List<SkillAccess>();

            foreach (var name in skillNames)
            {
                var access = await CheckAccessAsync(name, permissionContext, cancellationToken);
                result.Add(access);
            }

            return result;
        }

        /// <summary>检查单个 Skill 的访问权限</summary>
        public async Task<SkillAccess> CheckAccessAsync(
            string skillName,
            PermissionContext? permissionContext = null,
            CancellationToken cancellationToken = default)
        {
            if (_permissionService == null || permissionContext == null)
            {
                return new SkillAccess
                {
                    SkillName = skillName,
                    IsAllowed = false,
                    Reason = "Permission service not configured"
                };
            }

            try
            {
                var result = await _permissionService.EvaluateSkillAsync(skillName, permissionContext, cancellationToken);

                return new SkillAccess
                {
                    SkillName = skillName,
                    IsAllowed = result.IsAllowed,
                    RequiresConfirmation = result.NeedsConfirmation,
                    Reason = result.Reason
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check skill permission: {SkillName}", skillName);
                return new SkillAccess
                {
                    SkillName = skillName,
                    IsAllowed = false,
                    Reason = $"Permission check failed: {ex.Message}"
                };
            }
        }

        /// <summary>验证 Skill 内容的安全性</summary>
        public SkillSecurityResult ValidateContent(string content)
        {
            var warnings = new List<string>();
            var errors = new List<string>();

            // 检查危险模式
            var dangerousPatterns = new Dictionary<string, string>
            {
                [@"eval\s*\("] = "Use of eval() detected",
                [@"exec\s*\("] = "Use of exec() detected",
                [@"system\s*\("] = "System command execution detected",
                [@"subprocess"] = "Subprocess usage detected",
                [@"os\.system"] = "OS system call detected",
                [@"File\.DeleteAll"] = "Destructive file operation detected",
                [@"DROP\s+TABLE"] = "SQL DROP statement detected"
            };

            foreach (var (pattern, message) in dangerousPatterns)
            {
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
                {
                    warnings.Add(message);
                }
            }

            // 检查敏感信息模式
            var sensitivePatterns = new Dictionary<string, string>
            {
                [@"(?i)password\s*=\s*[""'][^""']+[""']"] = "Hardcoded password detected",
                [@"(?i)api[_-]?key\s*=\s*[""'][^""']+[""']"] = "Hardcoded API key detected",
                [@"(?i)secret\s*=\s*[""'][^""']+[""']"] = "Hardcoded secret detected"
            };

            foreach (var (pattern, message) in sensitivePatterns)
            {
                if (Regex.IsMatch(content, pattern))
                {
                    errors.Add(message);
                }
            }

            return new SkillSecurityResult
            {
                IsSecure = errors.Count == 0,
                Errors = errors,
                Warnings = warnings
            };
        }
    }

    /// <summary>Skill 访问结果</summary>
    public class SkillAccess
    {
        public string SkillName { get; set; } = string.Empty;
        public bool IsAllowed { get; set; }
        public bool RequiresConfirmation { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>Skill 安全验证结果</summary>
    public class SkillSecurityResult
    {
        public bool IsSecure { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
