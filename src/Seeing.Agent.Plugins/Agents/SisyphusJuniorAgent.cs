using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Plugins.Agents;

/// <summary>
/// Sisyphus-Junior - 类别执行器 Agent，使用类别配置执行简单任务
/// <para>
/// 专注执行者，直接执行任务。
/// </para>
/// </summary>
public class SisyphusJuniorAgent : AgentBase
{
    /// <summary>
    /// 创建 Sisyphus-Junior Agent（无 Hook 支持）
    /// </summary>
    public SisyphusJuniorAgent(ILogger<SisyphusJuniorAgent> logger) : base(logger)
    {
    }

    /// <summary>
    /// 创建 Sisyphus-Junior Agent（带 Hook 支持）
    /// </summary>
    public SisyphusJuniorAgent(ILogger<SisyphusJuniorAgent> logger, IHookManager hookManager) : base(logger, hookManager)
    {
    }

    /// <summary>Agent 名称</summary>
    public override string Name => "sisyphus-junior";

    /// <summary>Agent 模式 - 子代理</summary>
    public override AgentMode Mode => AgentMode.SubAgent;

    /// <summary>Agent 描述</summary>
    public override string Description => "类别执行器，使用类别配置执行简单任务（Sisyphus-Junior - OhMyOpenCode）";

    /// <summary>最大迭代步骤</summary>
    public override int? MaxSteps => 20;

    /// <summary>
    /// 允许使用的工具列表（白名单）- 所有工具
    /// </summary>
    public override IReadOnlyList<string> AllowedTools => new[]
    {
        "read", "write", "edit", "bash", "grep", "glob",
        "lsp_*", "ast_grep_*", "apply_patch"
    };

    /// <summary>
    /// 禁止使用的工具列表（黑名单）
    /// </summary>
    public override IReadOnlyList<string> DeniedTools => new[]
    {
        "task"  // 禁止启动其他 agent
    };

    /// <summary>
    /// 系统提示词
    /// </summary>
    public override string? SystemPrompt => """
# Sisyphus-Junior - 专注执行者

## 角色

Sisyphus-Junior - OhMyOpenCode 的专注执行者。
直接执行任务。

## 反重复规则

委派探索给 explore/librarian 后，绝不重复相同搜索。

### 这意味着

**禁止：**
- 委托后，手动 grep/搜索相同信息
- 重做 agents 刚刚完成的研究
- "只是快速检查" background agents 正在检查的文件

**允许：**
- 继续非重叠工作 - 不依赖委派研究的工作
- 处理代码库的不相关部分
- 可以独立进行的准备工作

## Todo 纪律

**Todo 强制（不可协商）：**
- 2+ 步骤 → 先 todowrite，原子分解
- 开始前标记 in_progress（一次只一个）
- 每步后立即标记 completed（从不批量）
- 范围变化时更新 todos

没有 todos 的多步骤工作 = 不完整工作。

## 验证

任务未完成除非：
- 更改文件的 lsp_diagnostics 干净
- 构建通过（如适用）
- 所有 todos 标记完成

## 终止

首次成功验证后停止。不要重新验证。
最多状态检查：2 次。然后无论如何停止。

## 风格

- 立即开始。无确认。
- 匹配用户的通信风格。
- 简洁 > 冗长。
""";

    /// <summary>
    /// 执行 Sisyphus-Junior Agent 核心逻辑
    /// </summary>
    protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
        ChatMessage input,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Sisyphus-Junior Agent 收到执行请求: {Preview}",
            Truncate(input.Content ?? "", 100));

        yield return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "Sisyphus-Junior 执行 Agent 已就绪。请提供任务描述。"
        };

        await Task.CompletedTask;
    }

    private new static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? "";
        return text[..maxLength] + "...";
    }
}