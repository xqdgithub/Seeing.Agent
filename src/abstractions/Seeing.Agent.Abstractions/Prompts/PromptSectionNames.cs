namespace Seeing.Agent.Abstractions.Prompts;

/// <summary>
/// 提示词分节名称常量（对应 <c>{{tools}}</c>、<c>{{agents}}</c> 等占位符分节）。
/// </summary>
public static class PromptSectionNames
{
    /// <summary>工具列表分节</summary>
    public const string Tools = "tools";

    /// <summary>技能列表分节</summary>
    public const string Skills = "skills";

    /// <summary>代理列表分节</summary>
    public const string Agents = "agents";

    /// <summary>环境信息分节</summary>
    public const string Environment = "environment";
}
