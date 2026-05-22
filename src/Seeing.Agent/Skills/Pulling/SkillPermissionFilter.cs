using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Skills.Pulling;

namespace Seeing.Agent.Skills
{
    /// <summary>
    /// Skill 权限过滤器 - 根据 Rules 过滤可用的 Skills
    /// </summary>
    public class SkillPermissionFilter
    {
        private readonly ILogger<SkillPermissionFilter> _logger;
        private readonly IRuleEngine? _ruleEngine;

        public SkillPermissionFilter(ILogger<SkillPermissionFilter> logger, IRuleEngine? ruleEngine = null)
        {
            _logger = logger;
            _ruleEngine = ruleEngine;
        }

        /// <summary>过滤 Skills 列表，返回允许访问的 Skills</summary>
        public async Task<IReadOnlyList<SkillAccess>> FilterAsync(
            IEnumerable<string> skillNames,
            string context,
            CancellationToken cancellationToken = default)
        {
            var result = new List<SkillAccess>();

            foreach (var name in skillNames)
            {
                var access = await CheckAccessAsync(name, context, cancellationToken);
                result.Add(access);
            }

            return result;
        }

        /// <summary>检查单个 Skill 的访问权限</summary>
        public async Task<SkillAccess> CheckAccessAsync(
            string skillName,
            string context,
            CancellationToken cancellationToken = default)
        {
            if (_ruleEngine == null)
            {
                return new SkillAccess
                {
                    SkillName = skillName,
                    IsAllowed = true,
                    Reason = "No rule engine configured"
                };
            }

            try
            {
                var permission = $"skill:{skillName}";
                var action = _ruleEngine.Evaluate(permission, context);

                return new SkillAccess
                {
                    SkillName = skillName,
                    IsAllowed = action != PermissionAction.Deny,
                    RequiresConfirmation = action == PermissionAction.Ask,
                    Reason = $"Rule evaluation: {action}"
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
