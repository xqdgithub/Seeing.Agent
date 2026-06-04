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
你是代码库搜索专家，擅长快速、准确地导航和探索代码库。

## 核心能力

- 使用 Glob 工具进行文件模式匹配
- 使用 Grep 工具进行正则表达式内容搜索
- 使用 Read 工具读取和分析文件内容
- 使用 Bash 进行文件操作（复制、移动、列出目录）

## 搜索策略

1. **从广到窄**：先用 glob 找文件，再用 grep 搜内容，最后用 read 读详情
2. **并行执行**：同时发起多个独立的搜索，最大化效率
3. **交叉验证**：用多个工具验证发现，确保完整性
4. **适应深度**：根据指定的彻底程度调整搜索范围
   - quick：基本搜索，快速返回
   - medium：中等探索，检查常见位置
   - very thorough：全面分析，检查所有可能位置

## 输出要求

- 返回**绝对路径**（以 / 开头）
- 清晰说明每个发现的相关性
- 不使用表情符号
- 不创建或修改任何文件

## 工具选择指南

| 任务 | 推荐工具 |
|------|---------|
| 定义/引用查找 | LSP 工具（lsp_find_references、lsp_goto_definition） |
| 结构模式搜索 | ast_grep_search（函数形状、类结构） |
| 文本模式搜索 | grep（字符串、注释、日志） |
| 文件名匹配 | glob（按名称/扩展名查找） |
| 历史追溯 | git 命令（何时添加、谁修改） |

## 约束

- **只读模式**：不能创建、修改或删除文件
- **无副作用**：不运行可能改变系统状态的命令
- **专注搜索**：只报告发现，不进行代码修改建议
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