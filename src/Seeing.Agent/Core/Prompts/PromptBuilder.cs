using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Skills;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 提示词构建器 - 统一构建系统提示词
/// <para>
/// 支持的占位符：
/// - {{tools}} - 工具列表
/// - {{agents}} - 代理列表
/// - {{skills}} - 技能列表
/// - {{environment}} - 环境信息（工作目录、平台、时间）
/// - {{instructions}} - AGENTS.md 指令
/// - 自定义变量 {{variable_name}}
/// </para>
/// </summary>
public class PromptBuilder
{
    private const string ToolsPlaceholder = "{{tools}}";
    private const string AgentsPlaceholder = "{{agents}}";
    private const string SkillsPlaceholder = "{{skills}}";
    private const string EnvironmentPlaceholder = "{{environment}}";
    private const string InstructionsPlaceholder = "{{instructions}}";

    private readonly SystemPromptProvider _systemPromptProvider;
    private readonly IInstructionLoader _instructionLoader;
    private readonly IAgentRegistry _agentRegistry;
    private readonly SkillManager _skillManager;

    public PromptBuilder(
        SystemPromptProvider systemPromptProvider,
        IInstructionLoader instructionLoader,
        IAgentRegistry agentRegistry,
        SkillManager skillManager)
    {
        _systemPromptProvider = systemPromptProvider;
        _instructionLoader = instructionLoader;
        _agentRegistry = agentRegistry;
        _skillManager = skillManager;
    }

    /// <summary>
    /// 构建完整的系统提示词
    /// </summary>
    /// <param name="context">提示词构建上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>构建后的完整提示词</returns>
    public async Task<string> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        // 1. 获取 Provider 特定模板（如果有）
        var basePrompt = context.Agent?.SystemPrompt;

        // 如果 Agent 没有定义系统提示词，使用 Provider 默认模板
        if (string.IsNullOrEmpty(basePrompt) && !string.IsNullOrEmpty(context.ProviderId) && !string.IsNullOrEmpty(context.ModelName))
        {
            basePrompt = _systemPromptProvider.GetTemplate(context.ProviderId, context.ModelName);
        }

        if (string.IsNullOrEmpty(basePrompt))
        {
            return string.Empty;
        }

        var result = basePrompt;

        // 2. 替换工具占位符
        if (result.Contains(ToolsPlaceholder) && context.Tools != null)
        {
            result = result.Replace(ToolsPlaceholder, BuildToolSection(context.Tools));
        }

        // 3. 替换代理占位符
        if (result.Contains(AgentsPlaceholder))
        {
            var agents = await _agentRegistry.GetAgentsAsync();
            result = result.Replace(AgentsPlaceholder, BuildAgentSection(agents, context.Agent?.Name));
        }

        // 4. 替换技能占位符
        if (result.Contains(SkillsPlaceholder))
        {
            var skills = _skillManager.GetAllSkillInfos().Values.ToList();
            result = result.Replace(SkillsPlaceholder, BuildSkillSection(skills));
        }

        // 5. 替换环境信息占位符
        if (result.Contains(EnvironmentPlaceholder))
        {
            result = result.Replace(EnvironmentPlaceholder, BuildEnvironmentSection(context));
        }

        // 6. 替换指令占位符
        if (result.Contains(InstructionsPlaceholder))
        {
            var instructions = await _instructionLoader.DiscoverAsync(context.WorkingDirectory ?? "", cancellationToken);
            var mergedInstructions = _instructionLoader.Merge(instructions);
            result = result.Replace(InstructionsPlaceholder, mergedInstructions);
        }

        // 7. 替换自定义变量
        foreach (var (key, value) in context.Variables)
        {
            result = result.Replace($"{{{{{key}}}}}", value);
        }

        // 8. 替换内置变量
        result = ReplaceBuiltinVariables(result, context);

        return result.Trim();
    }

    /// <summary>
    /// 同步构建（向后兼容）
    /// <para>注意：不支持 {{agents}}（需异步）、{{instructions}}（需异步）占位符</para>
    /// </summary>
    public string Build(string basePrompt, PromptContext context)
    {
        if (string.IsNullOrEmpty(basePrompt))
            return string.Empty;

        var result = basePrompt;

        if (context.Tools != null)
            result = result.Replace(ToolsPlaceholder, BuildToolSection(context.Tools));

        if (context.Agents != null)
            result = result.Replace(AgentsPlaceholder, BuildAgentSection(context.Agents, null));

        if (context.Skills != null)
            result = result.Replace(SkillsPlaceholder, BuildSkillSection(context.Skills.ToList()));

        // 环境信息同步可用
        result = result.Replace(EnvironmentPlaceholder, BuildEnvironmentSection(context));

        foreach (var (key, value) in context.Variables)
            result = result.Replace($"{{{{{key}}}}}", value);

        result = ReplaceBuiltinVariables(result, context);

        return result.Trim();
    }

    #region 内容构建方法（保持内聚）

    private string BuildToolSection(IEnumerable<FunctionSchema> tools)
    {
        var toolList = tools.ToList();
        if (toolList.Count == 0)
            return "暂无可用工具。";

        var sb = new StringBuilder();
        sb.AppendLine("## 可用工具");
        sb.AppendLine();
        sb.AppendLine("以下工具可供调用：");
        sb.AppendLine();

        foreach (var tool in toolList)
        {
            sb.AppendLine($"### {tool.Name}");
            if (!string.IsNullOrEmpty(tool.Description))
                sb.AppendLine(tool.Description);
            sb.AppendLine();

            if (tool.Parameters.HasValue)
            {
                var parameters = tool.Parameters.Value;
                if (parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("properties", out var properties))
                {
                    sb.AppendLine("**参数：**");
                    foreach (var prop in properties.EnumerateObject())
                    {
                        sb.AppendLine($"- `{prop.Name}`: {GetPropertyDescription(prop.Value)}");
                    }
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    private string BuildAgentSection(IEnumerable<AgentInfo> agents, string? currentAgentName)
    {
        var agentList = agents.ToList();
        var subAgents = agentList
            .Where(a => a.Mode == AgentMode.SubAgent && a.Name != currentAgentName)
            .ToList();

        if (subAgents.Count == 0)
            return "暂无可用子代理。";

        var sb = new StringBuilder();
        sb.AppendLine("## 可用代理");
        sb.AppendLine();
        sb.AppendLine("以下代理可供委托：");
        sb.AppendLine();

        foreach (var agent in subAgents)
        {
            var desc = agent.Description ?? "无描述";
            var shortDesc = desc.Split('.')[0];
            sb.AppendLine($"- **{agent.Name}**: {shortDesc}");
        }

        return sb.ToString();
    }

    private string BuildSkillSection(List<SkillInfo> skills)
    {
        if (skills.Count == 0)
            return "暂无可用技能。";

        var sb = new StringBuilder();
        sb.AppendLine("## 可用技能");
        sb.AppendLine();
        sb.AppendLine("以下技能可供使用：");
        sb.AppendLine();

        foreach (var skill in skills)
        {
            sb.AppendLine($"### {skill.Name}");
            sb.AppendLine(skill.Description);

            if (skill.Tags.Count > 0)
                sb.AppendLine($"**标签**: {string.Join(", ", skill.Tags)}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string BuildEnvironmentSection(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<env>");
        sb.AppendLine($"Working directory: {context.WorkingDirectory ?? "unknown"}");
        if (!string.IsNullOrEmpty(context.WorkspaceRoot))
            sb.AppendLine($"Workspace root: {context.WorkspaceRoot}");
        if (!string.IsNullOrEmpty(context.Platform))
            sb.AppendLine($"Platform: {context.Platform}");
        sb.AppendLine($"Today's date: {context.Timestamp:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(context.ModelName))
            sb.AppendLine($"Model: {context.ModelName}");
        sb.AppendLine("</env>");

        return sb.ToString();
    }

    private string ReplaceBuiltinVariables(string prompt, PromptContext context)
    {
        var result = prompt;

        if (!string.IsNullOrEmpty(context.ModelName))
            result = result.Replace("{{model}}", context.ModelName);

        if (!string.IsNullOrEmpty(context.SessionId))
            result = result.Replace("{{session_id}}", context.SessionId);

        if (!string.IsNullOrEmpty(context.WorkingDirectory))
            result = result.Replace("{{working_directory}}", context.WorkingDirectory);

        result = result.Replace("{{timestamp}}", context.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));

        return result;
    }

    private static string GetPropertyDescription(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
            return "未知类型";

        var sb = new StringBuilder();

        if (property.TryGetProperty("type", out var typeElement))
            sb.Append(typeElement.GetString() ?? "unknown");

        if (property.TryGetProperty("description", out var descElement))
        {
            var desc = descElement.GetString();
            if (!string.IsNullOrEmpty(desc))
                sb.Append($" - {desc}");
        }

        if (property.TryGetProperty("required", out var requiredElement) && requiredElement.GetBoolean())
            sb.Append(" (必需)");

        return sb.ToString();
    }

    #endregion
}
