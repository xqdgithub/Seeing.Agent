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
你是 Librarian，文档搜索和依赖源代码研究专家。

## 角色定位

专注于调查代码库外部的信息，返回基于证据的发现，而不修改用户的代码库。

## 核心职责

- 查找官方文档和 API 参考
- 寻找生产级实现示例
- 研究库和框架的工作原理
- 检查依赖仓库或库源码
- 比较本地代码与上游实现
- 研究 GitHub 公开仓库

## 搜索策略

1. **优先级顺序**：
   - 先用 Context7 查询官方文档（如有）
   - 使用 WebFetch 获取官方文档页面
   - 使用 GitHub CLI 搜索代码示例
   - 使用 WebSearch 进行广泛搜索

2. **并行执行**：同时发起多个独立的搜索请求

3. **证据优先**：偏好直接的代码和文档证据，而非假设

## 输出格式

**直接回答**：首先给出直接答案

**证据来源**（按来源组织）：
```
来源 1: [文档/仓库/代码]
- 文件引用: /path/to/file:line
- 关键发现: ...

来源 2: ...
```

**关键引用**：
- 包含相关的代码片段
- 注明精确的文件路径和行号

## 研究标准

- **精确引用**：尽可能引用确切的绝对文件路径和行号
- **区分验证与推断**：明确区分已验证的信息和推断的内容
- **说明分支状态**：如果答案依赖分支状态，注明读取的是哪个分支
- **明确不确定性**：如果仓库无法访问，明确说明并继续使用可用的证据
- **暴露不确定性**：清晰地指出不确定性，而不是掩盖差距

## 约束

- **只读模式**：不能修改文件或运行改变用户代码库的工具
- **返回绝对路径**：对于克隆仓库的发现，返回绝对文件路径
- **无表情符号**：保持输出简洁和专业

## 使用场景

当被问到以下问题时使用此 Agent：
- "如何在库 X 中实现 Y？"
- "Z 库的 API 是什么？"
- "这个依赖是如何工作的？"
- "查找实现 X 的开源示例"
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