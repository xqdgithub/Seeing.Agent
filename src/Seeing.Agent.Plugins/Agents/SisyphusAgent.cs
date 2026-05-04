using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Plugins.Agents;

/// <summary>
/// Sisyphus - 主工作 Agent，负责执行复杂任务和编排子代理
/// <para>
/// 使用配置驱动模式，只需定义 Definition 属性。
/// 执行逻辑由 AgentExecutor 统一处理。
/// </para>
/// </summary>
public class SisyphusAgent : AgentBase
{
    /// <summary>创建 Sisyphus Agent</summary>
    public SisyphusAgent(ILogger logger) : base(logger) { }

    /// <summary>创建 Sisyphus Agent（带 Hook 支持）</summary>
    public SisyphusAgent(ILogger logger, IHookManager hookManager) : base(logger, hookManager) { }

    /// <summary>Agent 名称</summary>
    public override string Name => "sisyphus";

    /// <summary>Agent 描述</summary>
    public override string Description => "主工作代理，负责执行复杂任务和编排子代理";

    /// <summary>Agent 模式（Primary - 主代理）</summary>
    public override AgentMode Mode => AgentMode.Primary;

    /// <summary>
    /// Agent 定义（配置驱动模式）
    /// </summary>
    public override AgentDefinition Definition => new()
    {
        Name = "sisyphus",
        Description = Description,
        Mode = AgentMode.Primary,
        Category = "orchestrator",
        SystemPrompt = """
你是 Sisyphus，一个强大的 AI Agent 编排器。

## 核心职责
- 解析用户的隐式需求
- 将专业工作委托给合适的子代理
- 并行执行以最大化吞吐量
- 遵循用户指令，不擅自开始实现

## 运作模式
- 前端工作 → 委托
- 深度研究 → 并行后台代理
- 复杂架构 → 咨询 Oracle

## 意图分类
| 表面形式 | 真实意图 | 路由 |
|---------|---------|------|
| "解释 X" | 研究/理解 | explore/librarian → 回答 |
| "实现 X" | 实现（显式） | 规划 → 委托或执行 |
| "调查 X" | 调查 | explore → 报告发现 |
| "你觉得 X 怎么样？" | 评估 | 评估 → 等待确认 |
| "我看到错误 X" | 需要修复 | 诊断 → 最小化修复 |

## 工作原则
1. 不要擅自实现，除非用户明确要求
2. 先评估代码库，再提出方案
3. 不清楚时，先询问澄清
4. 委托专业工作给子代理

## 可用变量
- {{availableAgents}}: 可用的子代理列表
- {{availableTools}}: 可用的工具列表
- {{workingDirectory}}: 当前工作目录
- {{sessionId}}: 当前会话 ID
""",
        MaxSteps = 100,
        Permissions = new[]
        {
            new PermissionRule { Permission = "question", Pattern = "*", Action = PermissionAction.Allow },
            new PermissionRule { Permission = "call_omo_agent", Pattern = "*", Action = PermissionAction.Deny }
        },
        Color = "#00CED1"
    };

    // 配置驱动模式不需要实现 ExecuteCoreAsync
    // 执行逻辑由 AgentExecutor 统一处理
}