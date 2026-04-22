using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 提示词构建上下文 - 包含工具、代理、技能等信息
/// </summary>
public class PromptContext
{
    /// <summary>可用工具列表</summary>
    public IEnumerable<FunctionSchema>? Tools { get; set; }

    /// <summary>可用代理列表</summary>
    public IEnumerable<Seeing.Agent.Core.Interfaces.AgentInfo>? Agents { get; set; }

    /// <summary>可用技能列表</summary>
    public IEnumerable<SkillInfo>? Skills { get; set; }

    /// <summary>自定义变量</summary>
    public Dictionary<string, string> Variables { get; set; } = new();

    /// <summary>当前模型名称</summary>
    public string? ModelName { get; set; }

    /// <summary>当前会话 ID</summary>
    public string? SessionId { get; set; }

    /// <summary>当前工作目录</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>创建时间</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;
}