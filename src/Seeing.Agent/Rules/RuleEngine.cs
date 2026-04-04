using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Hooks;
using System.Collections.Concurrent;

namespace Seeing.Agent.Rules
{
    /// <summary>
    /// 规则引擎实现 - 权限规则的评估和管理
    /// </summary>
    public class RuleEngine : IRuleEngine, IRuleEvaluator
    {
        private readonly ILogger<RuleEngine> _logger;
        private readonly IHookManager? _hookManager;
        // 使用并发字典替代 ConcurrentBag，以支持按键删除规则
        private readonly ConcurrentDictionary<string, PermissionRule> _rules = new();

        /// <summary>
        /// 创建规则引擎实例（无 Hook 支持）
        /// </summary>
        public RuleEngine(ILogger<RuleEngine> logger)
        {
            _logger = logger;
            _hookManager = null;
        }

        /// <summary>
        /// 创建规则引擎实例（带 Hook 支持）
        /// </summary>
        public RuleEngine(ILogger<RuleEngine> logger, IHookManager hookManager)
        {
            _logger = logger;
            _hookManager = hookManager;
        }

        /// <summary>
        /// 移除规则
        /// </summary>
        public bool RemoveRule(string permission, string pattern)
        {
            var key = $"{permission}:{pattern}";
            return _rules.TryRemove(key, out _);
        }

        /// <summary>
        /// 添加规则
        /// </summary>
        public void AddRule(PermissionRule rule)
        {
            if (rule == null)
            {
                _logger.LogWarning("尝试添加空规则，已忽略");
                return;
            }

            // 以 "Permission:Pattern" 作为唯一键，使用字典实现可删除性
            var key = $"{rule.Permission}:{rule.Pattern}";
            _rules[key] = rule;
            _logger.LogDebug("添加规则: Permission={Permission}, Pattern={Pattern}, Action={Action}",
                rule.Permission, rule.Pattern, rule.Action);
        }

        /// <summary>
        /// 从配置加载规则
        /// </summary>
        public void LoadFromConfig(Dictionary<string, object> config)
        {
            if (config == null || !config.TryGetValue("permissions", out var permissionsObj))
            {
                _logger.LogDebug("配置中未找到 permissions 节点");
                return;
            }

            _logger.LogInformation("从配置加载规则");
        }

        /// <summary>
        /// 合并规则集
        /// </summary>
        public void MergeRules(IEnumerable<PermissionRule> rules)
        {
            if (rules == null) return;

            foreach (var rule in rules)
            {
                AddRule(rule);
            }
        }

        /// <summary>
        /// 求值权限请求
        /// </summary>
        public PermissionAction Evaluate(string permission, string pattern)
        {
            return EvaluateWithDecision(permission, pattern).Action;
        }

        /// <summary>
        /// 求值权限请求（带详细信息）
        /// </summary>
        public async Task<PermissionDecision> EvaluateWithDecisionAsync(string permission, string pattern, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(permission))
            {
                _logger.LogWarning("权限名称为空，返回默认动作 Allow");
                return PermissionDecision.Allow("默认允许");
            }

            var matchingRules = _rules.Values
                .Where(r => r.Permission == permission && MatchesPattern(r.Pattern, pattern))
                .OrderByDescending(r => r.Pattern.Length)
                .ToList();

            if (matchingRules.Count == 0)
            {
                _logger.LogDebug("未找到匹配规则: Permission={Permission}, Pattern={Pattern}, 返回 Allow",
                    permission, pattern);
                return PermissionDecision.Allow("无匹配规则");
            }

            var matchedRule = matchingRules.First();
            _logger.LogDebug("匹配规则: Permission={Permission}, Pattern={Pattern}, Action={Action}",
                matchedRule.Permission, matchedRule.Pattern, matchedRule.Action);

            var decision = new PermissionDecision(matchedRule.Action, $"匹配规则: {matchedRule.Pattern}", matchedRule);

            // ========== Hook: permission.ask ==========
            // 当规则要求 Ask 时，触发 Hook 让用户/系统决定
            if (decision.Action == PermissionAction.Ask && _hookManager != null)
            {
                var hookOutput = new Dictionary<string, object>
                {
                    ["decision"] = decision.Action.ToString(),
                    ["reason"] = decision.Reason ?? string.Empty
                };

                var hookResult = await _hookManager.TriggerAsync(
                    HookPoints.PermissionAsk,
                    new Dictionary<string, object>
                    {
                        ["permission"] = permission,
                        ["pattern"] = pattern,
                        ["rule"] = matchedRule,
                        ["matchedPattern"] = matchedRule.Pattern
                    },
                    hookOutput,
                    cancellationToken);

                // Hook 可以覆盖决策
                if (hookOutput.TryGetValue("decision", out var decisionObj) && decisionObj is string decisionStr)
                {
                    if (Enum.TryParse<PermissionAction>(decisionStr, ignoreCase: true, out var overriddenAction))
                    {
                        _logger.LogDebug("权限决策被 Hook 覆盖: {Original} -> {New}", decision.Action, overriddenAction);
                        decision = new PermissionDecision(overriddenAction, hookOutput.TryGetValue("reason", out var reason) ? reason?.ToString() : "Hook 覆盖", matchedRule);
                    }
                }
            }

            return decision;
        }

        /// <summary>
        /// 求值权限请求（带详细信息，同步版本）
        /// </summary>
        public PermissionDecision EvaluateWithDecision(string permission, string pattern)
        {
            return EvaluateWithDecisionAsync(permission, pattern).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 评估单个权限请求（IRuleEvaluator 接口）
        /// </summary>
        PermissionDecision IRuleEvaluator.Evaluate(string permission, string pattern)
        {
            return EvaluateWithDecision(permission, pattern);
        }

        /// <summary>
        /// 评估工具调用权限
        /// </summary>
        public PermissionDecision EvaluateTool(string toolId, IExecutionContext? ctx = null)
        {
            var decision = EvaluateWithDecision("tool", toolId);
            
            _logger.LogDebug("工具权限评估: ToolId={ToolId}, Action={Action}", toolId, decision.Action);
            
            return decision;
        }

        /// <summary>
        /// 评估 Agent 行动权限
        /// </summary>
        public PermissionDecision EvaluateAction(string action, IDictionary<string, object>? context = null)
        {
            var decision = EvaluateWithDecision("action", action);
            
            _logger.LogDebug("行动权限评估: Action={Action}, Result={Result}", action, decision.Action);
            
            return decision;
        }

        /// <summary>
        /// 检查工具是否被禁用
        /// </summary>
        public bool IsToolDisabled(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            return Evaluate("tool", toolName) == PermissionAction.Deny;
        }

        /// <summary>
        /// 获取所有规则
        /// </summary>
        public IReadOnlyList<PermissionRule> GetRules() => _rules.Values.ToList().AsReadOnly();

        /// <summary>
        /// 模式匹配 - 支持通配符 (* 和 ?)
        /// </summary>
        private bool MatchesPattern(string rulePattern, string inputPattern)
        {
            if (rulePattern == "*") return true;
            if (string.IsNullOrEmpty(inputPattern)) return rulePattern == "*";

            return SimpleWildcardMatch(rulePattern, inputPattern);
        }

        /// <summary>
        /// 简单通配符匹配算法
        /// </summary>
        private bool SimpleWildcardMatch(string pattern, string input)
        {
            int patternIndex = 0;
            int inputIndex = 0;
            int starIndex = -1;
            int matchIndex = 0;

            while (inputIndex < input.Length)
            {
                if (patternIndex < pattern.Length &&
                    (pattern[patternIndex] == input[inputIndex] || pattern[patternIndex] == '?'))
                {
                    patternIndex++;
                    inputIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex;
                    matchIndex = inputIndex;
                    patternIndex++;
                }
                else if (starIndex != -1)
                {
                    patternIndex = starIndex + 1;
                    matchIndex++;
                    inputIndex = matchIndex;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }

            return patternIndex == pattern.Length;
        }
    }
}