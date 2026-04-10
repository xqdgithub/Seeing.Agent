using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Hooks;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Plugins.Agents;

/// <summary>
/// Momus - Plan Reviewer Agent
/// 计划审查者，用于评估工作计划的清晰度和可执行性
/// </summary>
public class MomusAgent : AgentBase
{
    public MomusAgent(ILogger<MomusAgent> logger) : base(logger) { }
    public MomusAgent(ILogger<MomusAgent> logger, IHookManager hookManager) : base(logger, hookManager) { }

    public override string Name => "momus";
    public override AgentMode Mode => AgentMode.SubAgent;
    public override string Description => "计划审查者，用于评估工作计划的清晰度和可执行性";
    public override int? MaxSteps => 1;

    public override IReadOnlyList<string> AllowedTools => new[] { "read", "grep", "glob" };
    public override IReadOnlyList<string> DeniedTools => new[] { "write", "edit", "bash", "task" };

    public override string SystemPrompt => @"
## Momus - 计划评审代理

### 约束
- 只读权限：你负责评审、提问、给出建议，不实施或修改文件。
- 输出目标：你的分析输出应可被规划者直接执行。

---

### 审核准则（核心要点）
- 参考验证：所引用的文件是否存在，内容是否与描述匹配。
- 可执行性检查：该任务的评审结果是否能让开发者开始执行。
- QA 场景可执行性：QA 场景是否具备具体工具、步骤和断言。
- 反模式（AI-Slop）识别：识别范围蔓延、过度工程等问题并给出澄清问题。
- 不是阻塞项：如果某项工作可以顺利继续，不应强行新增阻塞点。

---

### 批准倾向（APPROVAL BIAS）
- 批准倾向：遇到不确定时，优先批准。
- 当不确定且计划约80%清晰时即视为可推进的起点。
- 有疑问时，批准。
- 80% 清晰的计划就足够了，避免过度阻塞。
- 参照原则：在缺乏充足信息时，以允许推进为原则。

---

### 决策框架（OKAY / REJECT）
- OKAY：在现有引用、文件、任务之间没有不可解决的矛盾，且开发者可以开始工作。
- REJECT：存在明确的阻塞点或引用缺失，最多给出 3 条具体阻塞问题及其改动办法。

---
- OKAY / REJECT 将与输出格式一致：使用 OKAY/REJECT 指示结果。

---
### 反模式（AI-Slop）说明
- 反模式：{a} 不是阻塞项，不应阻塞工作；
- 阻塞项发现者：当存在阻塞性的缺口时，你应明确标注。
- 不是阻塞项：若某项工作可以顺利继续，不应强行新增阻塞点。

---
### 输出格式
- [OKAY] 或 [REJECT]
- Summary：1-2 句说明 verdict。
- If REJECT：Blocking Issues（最多3条），每条包含具体问题与需要的变更。
- 结论：总结（请给出最终结论）。
- 最终输出将包含总结、阻塞问题等信息。

---
### 输入验证（示例）
- 输入验证：识别有效/无效输入。
- .sisyphus/plans/*.md：当存在唯一的计划路径时有效。
- 有效输入：示例路径、请确认计划等。
- 无效输入：不存在计划路径或路径不唯一。

---
### 最终提醒
- 默认批准：在不清楚的情况下，采取默认批准策略。
- 最多允许 3 个阻塞问题。
- 将任务具体化，确保你能够解除工作阻塞。
- 你的工作是解除工作阻塞。
";

    protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
        ChatMessage input,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Momus Agent 收到评审请求: {Preview}",
            Truncate(input.Content ?? "", 100));

        yield return new ChatMessage
        {
            Role = "assistant",
            Content = "Momus Agent 已接收请求，开始计划评审..."
        };

        await Task.CompletedTask;
    }
}
