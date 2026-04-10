using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Plugins.Agents;

/// <summary>
/// Multimodal-Looker - PDF/图像分析 Agent，用于从媒体文件提取信息
/// <para>
/// 专注于解读无法作为纯文本读取的媒体文件，提供：
/// - PDF 文档的文本、结构、表格、数据提取
/// - 图像内容的布局、UI 元素、文本、图表描述
/// - 关系图、流程图、架构图的解释
/// </para>
/// <para>
/// 使用场景：
/// - Read 工具无法解读的媒体文件
/// - 从文档中提取特定信息或摘要
/// - 描述图像或图表中的视觉内容
/// - 需要分析/提取数据而非原始文件内容
/// </para>
/// </summary>
public class MultimodalLookerAgent : AgentBase
{
    /// <summary>
    /// 创建 Multimodal-Looker Agent 实例
    /// </summary>
    public MultimodalLookerAgent(ILogger<MultimodalLookerAgent> logger) : base(logger)
    {
    }

    /// <summary>Agent 名称</summary>
    public override string Name => "multimodal-looker";

    /// <summary>Agent 模式 - 子代理，只能被其他 Agent 调用</summary>
    public override AgentMode Mode => AgentMode.SubAgent;

    /// <summary>Agent 描述</summary>
    public override string Description =>
        "分析媒体文件（PDF、图像、图表），提供超越原始文本的解读能力。" +
        "从文档中提取特定信息或摘要，描述视觉内容。" +
        "当需要分析/提取数据而非原始文件内容时使用。" +
        " (Multimodal-Looker - OhMyOpenCode)";

    /// <summary>最大迭代步骤 - 单次分析任务</summary>
    public override int? MaxSteps => 1;

    /// <summary>
    /// 允许使用的工具列表（白名单）- 仅读取工具
    /// </summary>
    public override IReadOnlyList<string> AllowedTools => new[]
    {
        "read"  // 文件读取（包含多模态能力）
    };

    /// <summary>
    /// 禁止使用的工具列表（黑名单）- 写入和执行类工具
    /// </summary>
    public override IReadOnlyList<string> DeniedTools => new[]
    {
        "write",     // 文件写入
        "edit",      // 文件编辑
        "bash",      // Shell 命令执行
        "task"       // 子任务派发
    };

    /// <summary>
    /// 系统提示词 - Multimodal-Looker 的核心行为准则
    /// </summary>
    public override string? SystemPrompt => """
你解读无法作为纯文本读取的媒体文件。

你的工作：检查附加的文件，仅提取被请求的内容。

何时使用你：
- Read 工具无法解读的媒体文件
- 从文档中提取特定信息或摘要
- 描述图像或图表中的视觉内容
- 需要分析/提取数据而非原始文件内容

何时不应使用你：
- 需要精确内容的源代码或纯文本文件（使用 Read）
- 后续需要编辑的文件（需要 Read 的原始内容）
- 无需解读的简单文件读取

你如何工作：
1. 接收文件路径和描述要提取内容的 goal
2. 深入阅读和分析文件
3. 仅返回相关的提取信息
4. 主 Agent 从不处理原始文件 - 你节省上下文 token

对于 PDF：提取文本、结构、表格、特定章节的数据
对于图像：描述布局、UI 元素、文本、图表、图表
对于图表：解释描绘的关系、流程、架构

响应规则：
- 直接返回提取的信息，无开场白
- 若信息未找到，清楚说明缺失内容
- 匹配请求的语言
- 对 goal 要详尽，其他内容要简洁

你的输出直接发送给主 Agent 用于后续工作。
""";

    /// <summary>
    /// 执行 Multimodal-Looker Agent 核心逻辑
    /// </summary>
    protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
        ChatMessage input,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Multimodal-Looker 是配置型代理，实际分析由框架委托给 LLM 服务
        _logger.LogInformation(
            "Multimodal-Looker Agent 收到分析请求: {Preview}",
            Truncate(input.Content ?? "", 100));

        yield return new ChatMessage
        {
            Role = "assistant",
            Content = "Multimodal-Looker Agent 已接收请求，开始分析媒体文件..."
        };

        // 实际的 LLM 调用和多模态分析由外部 AgentRuntime/Orchestrator 完成
        await Task.CompletedTask;
    }
}