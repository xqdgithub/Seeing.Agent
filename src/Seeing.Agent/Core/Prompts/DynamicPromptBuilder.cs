using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text;
using System.Text.Json;
// 使用别名解决命名冲突
using PromptAgentInfo = Seeing.Agent.Core.Models.AgentDefinition;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 动态提示词构建器 - 用于构建 Agent 系统提示词
/// <para>
/// 支持占位符替换：
/// - {{tools}} - 工具列表
/// - {{agents}} - 代理列表
/// - {{skills}} - 技能列表
/// - {{variables}} - 自定义变量
/// </para>
/// </summary>
public class DynamicPromptBuilder
{
    /// <summary>工具占位符</summary>
    private const string ToolsPlaceholder = "{{tools}}";

    /// <summary>代理占位符</summary>
    private const string AgentsPlaceholder = "{{agents}}";

    /// <summary>技能占位符</summary>
    private const string SkillsPlaceholder = "{{skills}}";

    /// <summary>
    /// 构建完整的系统提示词
    /// </summary>
    /// <param name="basePrompt">基础提示词模板</param>
    /// <param name="context">提示词构建上下文</param>
    /// <returns>构建后的完整提示词</returns>
    public string Build(string basePrompt, PromptContext context)
    {
        if (string.IsNullOrEmpty(basePrompt))
        {
            return string.Empty;
        }

        var result = basePrompt;

        // 替换工具占位符
        if (context.Tools != null)
        {
            var toolSection = BuildToolSection(context.Tools);
            result = result.Replace(ToolsPlaceholder, toolSection);
        }

        // 替换代理占位符
        if (context.Agents != null)
        {
            var agentSection = BuildAgentSection(context.Agents);
            result = result.Replace(AgentsPlaceholder, agentSection);
        }

        // 替换技能占位符
        if (context.Skills != null)
        {
            var skillSection = BuildSkillSection(context.Skills);
            result = result.Replace(SkillsPlaceholder, skillSection);
        }

        // 替换自定义变量
        foreach (var (key, value) in context.Variables)
        {
            var placeholder = $"{{{{{key}}}}}";
            result = result.Replace(placeholder, value);
        }

        // 替换内置变量
        result = ReplaceBuiltinVariables(result, context);

        return result.Trim();
    }

    /// <summary>
    /// 构建工具列表部分
    /// </summary>
    /// <param name="tools">工具 Schema 列表</param>
    /// <returns>格式化的工具列表文本</returns>
    public string BuildToolSection(IEnumerable<FunctionSchema> tools)
    {
        if (tools == null)
        {
            return string.Empty;
        }

        var toolList = tools.ToList();
        if (toolList.Count == 0)
        {
            return "暂无可用工具。";
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 可用工具");
        sb.AppendLine();
        sb.AppendLine("以下工具可供调用：");
        sb.AppendLine();

        foreach (var tool in toolList)
        {
            sb.AppendLine($"### {tool.Name}");
            if (!string.IsNullOrEmpty(tool.Description))
            {
                sb.AppendLine(tool.Description);
            }
            sb.AppendLine();

            // 添加参数信息
            if (tool.Parameters.HasValue)
            {
                var parameters = tool.Parameters.Value;
                if (parameters.ValueKind == JsonValueKind.Object)
                {
                    if (parameters.TryGetProperty("properties", out var properties))
                    {
                        sb.AppendLine("**参数：**");
                        foreach (var prop in properties.EnumerateObject())
                        {
                            var propName = prop.Name;
                            var propDesc = GetPropertyDescription(prop.Value);
                            sb.AppendLine($"- `{propName}`: {propDesc}");
                        }
                        sb.AppendLine();
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建代理列表部分
    /// </summary>
    /// <param name="agents">代理信息列表</param>
    /// <returns>格式化的代理列表文本</returns>
    public string BuildAgentSection(IEnumerable<PromptAgentInfo> agents)
    {
        if (agents == null)
        {
            return string.Empty;
        }

        var agentList = agents.ToList();
        if (agentList.Count == 0)
        {
            return "暂无可用代理。";
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 可用代理");
        sb.AppendLine();
        sb.AppendLine("以下代理可供委托：");
        sb.AppendLine();

        // 按模式分组显示
        var primaryAgents = agentList.Where(a => a.Mode == AgentMode.Primary || a.Mode == AgentMode.All);
        var subAgents = agentList.Where(a => a.Mode == AgentMode.SubAgent);

        if (primaryAgents.Any())
        {
            sb.AppendLine("### 主代理");
            foreach (var agent in primaryAgents)
            {
                var desc = agent.Description ?? "无描述";
                var shortDesc = desc.Split('.')[0]; // 取第一句话作为简短描述
                sb.AppendLine($"- **{agent.Name}**: {shortDesc}");
                if (!string.IsNullOrEmpty(agent.Model?.ToString()))
                {
                    sb.AppendLine($"  - 模型: {agent.Model}");
                }
            }
            sb.AppendLine();
        }

        if (subAgents.Any())
        {
            sb.AppendLine("### 子代理");
            foreach (var agent in subAgents)
            {
                var desc = agent.Description ?? "无描述";
                var shortDesc = desc.Split('.')[0];
                sb.AppendLine($"- **{agent.Name}**: {shortDesc}");
                if (!string.IsNullOrEmpty(agent.Category))
                {
                    sb.AppendLine($"  - 分类: {agent.Category}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建技能列表部分
    /// </summary>
    /// <param name="skills">技能信息列表</param>
    /// <returns>格式化的技能列表文本</returns>
    public string BuildSkillSection(IEnumerable<SkillInfo> skills)
    {
        if (skills == null)
        {
            return string.Empty;
        }

        var skillList = skills.ToList();
        if (skillList.Count == 0)
        {
            return "暂无可用技能。";
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 可用技能");
        sb.AppendLine();
        sb.AppendLine("以下技能可供使用：");
        sb.AppendLine();

        foreach (var skill in skillList)
        {
            sb.AppendLine($"### {skill.Name}");
            sb.AppendLine(skill.Description);

            if (skill.Tags.Count > 0)
            {
                sb.AppendLine($"**标签**: {string.Join(", ", skill.Tags)}");
            }

            if (skill.Requires.Count > 0)
            {
                sb.AppendLine($"**依赖**: {string.Join(", ", skill.Requires)}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 替换内置变量
    /// </summary>
    private string ReplaceBuiltinVariables(string prompt, PromptContext context)
    {
        var result = prompt;

        // 替换模型名称
        if (!string.IsNullOrEmpty(context.ModelName))
        {
            result = result.Replace("{{model}}", context.ModelName);
        }

        // 替换会话 ID
        if (!string.IsNullOrEmpty(context.SessionId))
        {
            result = result.Replace("{{session_id}}", context.SessionId);
        }

        // 替换工作目录
        if (!string.IsNullOrEmpty(context.WorkingDirectory))
        {
            result = result.Replace("{{working_directory}}", context.WorkingDirectory);
        }

        // 替换时间戳
        result = result.Replace("{{timestamp}}", context.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));

        return result;
    }

    /// <summary>
    /// 获取属性的描述信息
    /// </summary>
    private string GetPropertyDescription(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
        {
            return "未知类型";
        }

        var sb = new StringBuilder();

        // 获取类型
        if (property.TryGetProperty("type", out var typeElement))
        {
            sb.Append(typeElement.GetString() ?? "unknown");
        }

        // 获取描述
        if (property.TryGetProperty("description", out var descElement))
        {
            var desc = descElement.GetString();
            if (!string.IsNullOrEmpty(desc))
            {
                sb.Append($" - {desc}");
            }
        }

        // 检查是否必需
        if (property.TryGetProperty("required", out var requiredElement) && requiredElement.GetBoolean())
        {
            sb.Append(" (必需)");
        }

        return sb.ToString();
    }
}