using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Core.Generation
{
    /// <summary>
    /// Agent 模板引擎 - 渲染模板中的变量占位符
    /// 支持 {{VariableName}} 语法，带默认值 {{VariableName:defaultValue}}
    /// </summary>
    public class AgentTemplateEngine
    {
        private static readonly Regex VariablePattern = new(
            @"\{\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::(?<default>[^}]*))?\}\}",
            RegexOptions.Compiled);

        /// <summary>渲染模板</summary>
        public TemplateRenderResult Render(
            string template,
            Dictionary<string, string> variables,
            List<TemplateVariable>? requiredVariables = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var usedVariables = new HashSet<string>();

            // 验证必需变量
            if (requiredVariables != null)
            {
                foreach (var rv in requiredVariables.Where(v => v.IsRequired))
                {
                    if (!variables.ContainsKey(rv.Name) && string.IsNullOrEmpty(rv.DefaultValue))
                    {
                        errors.Add($"Required variable '{rv.Name}' is missing: {rv.Description}");
                    }
                    // 验证正则
                    if (rv.ValidationPattern != null && variables.TryGetValue(rv.Name, out var value))
                    {
                        if (!Regex.IsMatch(value, rv.ValidationPattern))
                        {
                            errors.Add($"Variable '{rv.Name}' value '{value}' does not match pattern '{rv.ValidationPattern}'");
                        }
                    }
                }
            }

            // 渲染模板
            var result = VariablePattern.Replace(template, match =>
            {
                var name = match.Groups["name"].Value;
                var defaultVal = match.Groups["default"].Success ? match.Groups["default"].Value : null;
                usedVariables.Add(name);

                if (variables.TryGetValue(name, out var value))
                {
                    return value;
                }

                if (defaultVal != null)
                {
                    return defaultVal;
                }

                // 查找 requiredVariables 中的默认值
                var rvDef = requiredVariables?.FirstOrDefault(v => v.Name == name);
                if (rvDef?.DefaultValue != null)
                {
                    return rvDef.DefaultValue;
                }

                warnings.Add($"Variable '{name}' not provided and has no default value");
                return match.Value; // 保留原始占位符
            });

            // 检查未使用的变量
            foreach (var kv in variables)
            {
                if (!usedVariables.Contains(kv.Key))
                {
                    warnings.Add($"Variable '{kv.Key}' was provided but not used in template");
                }
            }

            return new TemplateRenderResult
            {
                RenderedContent = result,
                Errors = errors,
                Warnings = warnings,
                VariablesUsed = usedVariables.ToList()
            };
        }

        /// <summary>提取模板中所有变量</summary>
        public List<TemplateVariableInfo> ExtractVariables(string template)
        {
            var variables = new List<TemplateVariableInfo>();
            var matches = VariablePattern.Matches(template);

            foreach (Match match in matches)
            {
                var name = match.Groups["name"].Value;
                var hasDefault = match.Groups["default"].Success;
                var defaultVal = hasDefault ? match.Groups["default"].Value : null;

                // 避免重复
                if (variables.Any(v => v.Name == name)) continue;

                variables.Add(new TemplateVariableInfo
                {
                    Name = name,
                    HasDefaultValue = hasDefault,
                    DefaultValue = defaultVal
                });
            }

            return variables;
        }

        /// <summary>验证模板语法</summary>
        public TemplateValidationResult ValidateTemplate(string template)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(template))
            {
                errors.Add("Template is empty");
                return new TemplateValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
            }

            // 检查未闭合的占位符
            var openCount = Regex.Matches(template, @"\{\{").Count;
            var closeCount = Regex.Matches(template, @"\}\}").Count;
            if (openCount != closeCount)
            {
                errors.Add($"Mismatched placeholders: {openCount} opening {{{{ but {closeCount}}} closing }}}}");
            }

            // 检查无效变量名
            var invalidPattern = new Regex(@"\{\{([^}:][^}]*)\}\}", RegexOptions.Compiled);
            foreach (Match match in invalidPattern.Matches(template))
            {
                var inner = match.Groups[1].Value.Trim();
                if (!Regex.IsMatch(inner, @"^[A-Za-z_][A-Za-z0-9_]*(:[^}]*)?$"))
                {
                    warnings.Add($"Invalid variable syntax: {match.Value}");
                }
            }

            return new TemplateValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                Warnings = warnings
            };
        }
    }

    /// <summary>模板渲染结果</summary>
    public class TemplateRenderResult
    {
        public string RenderedContent { get; set; } = string.Empty;
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> VariablesUsed { get; set; } = new();
        public bool HasErrors => Errors.Count > 0;
    }

    /// <summary>模板变量信息</summary>
    public class TemplateVariableInfo
    {
        public string Name { get; set; } = string.Empty;
        public bool HasDefaultValue { get; set; }
        public string? DefaultValue { get; set; }
    }

    /// <summary>模板验证结果</summary>
    public class TemplateValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
