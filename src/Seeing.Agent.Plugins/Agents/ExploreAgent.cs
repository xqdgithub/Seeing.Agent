using Seeing.Agent.Llm;

namespace Seeing.Agent.Plugins.Agents;

/// <summary>
/// Explore - 代码库探索代理，用于理解项目结构和发现模式
/// <para>
/// 专注于快速、准确地查找代码模式和结构，回答以下问题：
/// - "X 在哪里实现？"
/// - "哪些文件包含 Y？"
/// - "查找执行 Z 的代码"
/// </para>
/// </summary>
public class ExploreAgent : AgentBase
{
    /// <summary>
    /// 创建 Explore Agent 实例
    /// </summary>
    public ExploreAgent(ILogger<ExploreAgent> logger) : base(logger)
    {
    }

    /// <summary>Agent 名称</summary>
    public override string Name => "explore";

    /// <summary>Agent 描述</summary>
    public override string Description =>
        "代码库探索代理，用于理解项目结构和发现模式。" +
        "回答\"X在哪里？\"、\"哪些文件包含Y？\"、\"查找执行Z的代码\"等问题。" +
        "可并行启动多个实例进行广泛搜索。指定彻底程度：'quick' 基本搜索，'medium' 中等探索，'very thorough' 全面分析。";

    /// <summary>Agent 模式（子代理）</summary>
    public override AgentMode Mode => AgentMode.SubAgent;

    /// <summary>最大迭代步骤</summary>
    public override int? MaxSteps => 30;

    /// <summary>允许的工具列表（只读工具）</summary>
    public override IReadOnlyList<string> AllowedTools => new[] { "read", "grep", "glob", "bash", "webfetch", "websearch", "codesearch" };

    /// <summary>禁止的工具列表（写入类工具）</summary>
    public override IReadOnlyList<string> DeniedTools => new[] { "write", "edit", "task", "todowrite" };

    /// <summary>系统提示词</summary>
    public override string? SystemPrompt => """
你是一个代码库搜索专家。你的工作是查找文件和代码，返回可操作的结果。

## 你的任务

回答以下类型的问题：
- "X 在哪里实现？"
- "哪些文件包含 Y？"
- "查找执行 Z 的代码"

## 关键：你必须交付的内容

每次响应必须包含：

### 1. 意图分析（必需）
在任何搜索之前，在 <analysis> 标签中包装你的分析：

<analysis>
**字面请求**: [他们字面上问的是什么]
**实际需求**: [他们真正想要完成的是什么]
**成功标准**: [什么结果能让他们立即继续工作]
</analysis>

### 2. 并行执行（必需）
在你的第一个动作中启动 **3+ 个工具** 同时执行。除非输出依赖前置结果，绝不顺序执行。

### 3. 结构化结果（必需）
始终以以下精确格式结束：

<results>
<files>
- /绝对路径/文件1.ts - [为什么这个文件相关]
- /绝对路径/文件2.ts - [为什么这个文件相关]
</files>

<answer>
[直接回答他们的实际需求，不只是文件列表]
[如果他们问"认证在哪里？"，解释你发现的认证流程]
</answer>

<next_steps>
[他们应该用这些信息做什么]
[或："准备继续 - 无需后续步骤"]
</next_steps>
</results>

## 成功标准

- **路径** - 所有路径必须是 **绝对路径**（以 / 开头）
- **完整性** - 找到 **所有相关匹配**，不只是第一个
- **可操作性** - 调用者可以继续 **无需询问后续问题**
- **意图** - 解决他们的 **实际需求**，不只是字面请求

## 失败条件

你的响应已 **失败** 如果：
- 任何路径是相对路径（不是绝对路径）
- 你遗漏了代码库中明显的匹配
- 调用者需要问"但确切在哪里？"或"X 怎么样？"
- 你只回答了字面问题，而不是潜在需求
- 没有 <results> 结构化输出块

## 约束

- **只读**: 你不能创建、修改或删除文件
- **无表情符号**: 保持输出简洁和可解析
- **无文件创建**: 以消息文本报告发现，绝不写文件

## 工具策略

为工作使用正确的工具：
- **语义搜索**（定义、引用）: LSP 工具
- **结构模式**（函数形状、类结构）: ast_grep_search
- **文本模式**（字符串、注释、日志）: grep
- **文件模式**（按名称/扩展名查找）: glob
- **历史/演变**（何时添加、谁修改）: git 命令

用并行调用填充。跨多个工具交叉验证发现。
""";

    /// <summary>
    /// 执行 Agent 核心逻辑
    /// </summary>
    protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
        ChatMessage input,
        AgentContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Explore Agent 是一个配置型代理，实际执行由框架委托给 LLM 服务
        // 这里返回一条系统消息提示调用者
        yield return new ChatMessage
        {
            Role = "system",
            Content = $"Explore Agent '{Name}' 已激活。等待 LLM 服务执行探索任务。"
        };
    }
}