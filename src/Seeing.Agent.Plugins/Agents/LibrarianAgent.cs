using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Plugins.Agents;

/// <summary>
/// Librarian - 文档搜索代理，用于查找外部文档和实现示例
/// </summary>
public class LibrarianAgent : AgentBase
{
    /// <summary>创建 Librarian Agent</summary>
    public LibrarianAgent(ILogger logger) : base(logger) { }

    /// <summary>Agent 名称</summary>
    public override string Name => "librarian";

    /// <summary>Agent 描述</summary>
    public override string Description => "文档搜索代理，查找外部文档和实现示例";

    /// <summary>Agent 模式（子代理）</summary>
    public override AgentMode Mode => AgentMode.SubAgent;

    /// <summary>最大迭代步骤</summary>
    public override int? MaxSteps => 20;

    /// <summary>允许使用的工具</summary>
    public override IReadOnlyList<string> AllowedTools => new[] { "webfetch", "grep", "glob", "read" };

    /// <summary>系统提示词</summary>
    public override string? SystemPrompt => """
你是 Librarian，一个文档搜索专家。

## 核心职责
- 查找官方文档和 API 参考
- 寻找生产级实现示例
- 提供最佳实践建议

## 搜索策略
1. 优先搜索官方文档
2. 查找 GitHub 上的高质量实现
3. 提取关键代码片段

## 输出格式
返回结构化的搜索结果：
- 文档链接
- 关键代码示例
- 最佳实践要点
""";

    /// <summary>
    /// 执行核心逻辑
    /// </summary>
    protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
        ChatMessage input,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "Librarian Agent 已就绪。请告诉我您需要查找什么文档或示例。"
        };
        await Task.CompletedTask;
    }
}