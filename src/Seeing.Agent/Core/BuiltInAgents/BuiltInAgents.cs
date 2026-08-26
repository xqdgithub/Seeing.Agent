using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

using Seeing.Agent.Abstractions.Permissions;
namespace Seeing.Agent.Core.BuiltInAgents
{
    /// <summary>
    /// 内置 Agent 定义 - 提供默认代理配置
    /// <para>
    /// - build: 默认主代理，执行工具，拥有完整权限
    /// - plan: 计划模式，禁用编辑工具
    /// - explore: 探索代理，快速代码库搜索
    /// - general: 通用代理，研究复杂问题
    /// </para>
    /// </summary>
    public static class BuiltInAgents
    {
        /// <summary>
        /// 获取所有内置 Agent 定义
        /// </summary>
        public static IEnumerable<Seeing.Agent.Abstractions.Agents.AgentDefinition> GetBuiltInAgents()
        {
            yield return CreateBuildAgent();
            yield return CreatePlanAgent();
            yield return CreateExploreAgent();
            yield return CreateGeneralAgent();
            yield return CreateSummaryAgent();
        }

        /// <summary>
        /// 创建 build Agent - 默认主代理
        /// </summary>
        private static Seeing.Agent.Abstractions.Agents.AgentDefinition CreateBuildAgent()
        {
            return new Seeing.Agent.Abstractions.Agents.AgentDefinition
            {
                Name = "build",
                Description = "默认主代理。拥有完整权限，可执行所有工具（包括 MCP 工具、文件操作、Shell 命令）。" +
                    "适用于需要完整能力的任务，如代码编写、文件编辑、命令执行等。",
                Mode = AgentMode.All,
                MaxSteps=64,
                Temperature = 0.7,
                IsNative = true,
                IsHidden = false,
                SystemPrompt = """
## 身份
- 你的名字是「小See」，一个智能助手。被问及身份时回答「小See」。

## 风格
- 简洁直接：除非用户要求，回答保持简短；简单问题用一个词或一句话回答
- 不使用表情符号，不说"我来…/让我…"之类的开场白，直接开始工作

## 工作原则
- 用户要求时才执行操作；被问"如何做"时先回答问题，不立即动手
- 修改代码前先理解文件约定并模仿现有模式，遵循安全最佳实践（不暴露或记录密钥）
- 完成文件编辑后直接停止，不解释做了什么

## 工具使用
- 多个独立请求可并行批量调用工具
- 优先使用专用工具而非 shell 命令（如用 Read/Edit 而非 cat/sed）
- 探索代码库时优先委托子代理

## 任务管理
- 3 步以上的多步任务用 TodoWrite 跟踪进度，完成立即标记 completed

## 代码引用
- 引用代码时用 `file_path:line_number` 格式

## 安全
- 不生成或猜测 URL（除非确定是编程用途）；无法帮助时简短说明并提供替代方案
""",
                PermissionRules = new List<PermissionRuleEntry>
                {
                    // 允许所有工具
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0),
                    // 允许所有 MCP 工具
                    PermissionRuleEntry.Allow(PermissionKind.McpTool, "*", 0),
                    // 允许文件操作
                    PermissionRuleEntry.Allow(PermissionKind.File, "*", 0),
                    // 允许 Shell 命令
                    PermissionRuleEntry.Allow(PermissionKind.Shell, "*", 0),
                    // 允许网络请求
                    PermissionRuleEntry.Allow(PermissionKind.Network, "*", 0),
                    // 允许技能调用
                    PermissionRuleEntry.Allow(PermissionKind.Skill, "*", 0),
                    // 允许子代理调用
                    PermissionRuleEntry.Allow(PermissionKind.Agent, "*", 0),
                },
                // 默认效果：允许（安全默认，因为 build 是主代理）
                PermissionDefaultEffect = PermissionEffect.Ask,
                Tags = new List<string> { "primary", "full-access", "default" }
            };
        }

        /// <summary>
        /// 创建 plan Agent - 计划模式
        /// </summary>
        private static Seeing.Agent.Abstractions.Agents.AgentDefinition CreatePlanAgent()
        {
            return new Seeing.Agent.Abstractions.Agents.AgentDefinition
            {
                Name = "plan",
                Description = "计划模式代理。专注于分析和规划，只允许读取操作和计划文件管理。" +
                    "允许使用 skill 加载技能、todowrite 管理任务列表、task 委托子代理。" +
                    "文件写入仅限于 .plans/ 目录和项目根目录下的 .md 文件（计划文档）。" +
                    "禁用 Shell 命令、代码编辑和 MCP 工具。",
                Mode = AgentMode.All,
                IsNative = true,
                MaxSteps = 64,
                Temperature = 0.7,
                IsHidden = false,
                SystemPrompt = """
# 计划模式

当前处于只读规划阶段：仅允许读取、搜索、委托 explore 代理，以及维护 .plans/ 目录下的计划文档。禁止一切修改系统的操作（权限已强制）。

## 职责
- 通过阅读、搜索和委托 explore 代理，产出全面而简洁的结构化实施计划
- 需求模糊或存在多种实现方式时，向用户提问澄清，不要对用户意图做大的假设
- 实施前向用户展示经过研究的计划，解决遗留问题

## 工作流程
理解需求 → 探索代码库 → 澄清问题 → 制定计划 → 标记风险与注意事项

## 输出格式

```markdown
## 目标
[简洁描述要实现的目标]

## 分析发现
[从代码库探索中发现的关键信息]

## 实施计划
1. [步骤 1] - [文件/位置]
...

## 待确认问题
- [问题 1]

## 风险和注意事项
- [风险 1]：[缓解措施]

## 验收标准
- [ ] [标准 1]
```
""",
                PermissionRules = new List<PermissionRuleEntry>
                {
                    // ===== 允许只读工具 =====
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "read", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "grep", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "glob", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "webfetch", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "websearch", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "codesearch", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "question", 0),
                    
                    // ===== 允许规划相关工具 =====
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "skill", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "todowrite", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "task", 0),
                    
                    // ===== 文件权限 =====
                    // 允许读取所有文件
                    PermissionRuleEntry.Allow(PermissionKind.File, "*", 0),
                    // 允许写入 .plans/ 目录下的 .md 文件（计划文档）
                    PermissionRuleEntry.Allow(PermissionKind.File, ".plans/**/*.md", 10),
                    // 允许写入项目根目录下的 .md 文件
                    PermissionRuleEntry.Allow(PermissionKind.File, "*.md", 5),
                    
                    // ===== 禁用危险操作 =====
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "edit", 100),
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "bash", 100),
                    PermissionRuleEntry.Deny(PermissionKind.McpTool, "*", 100),
                    PermissionRuleEntry.Deny(PermissionKind.Shell, "*", 100),
                },
                PermissionDefaultEffect = PermissionEffect.Deny,
                Tags = new List<string> { "planning", "readonly", "safe", "structured" }
            };
        }

        /// <summary>
        /// 创建 explore Agent - 代码库探索
        /// </summary>
        private static Seeing.Agent.Abstractions.Agents.AgentDefinition CreateExploreAgent()
        {
            return new Seeing.Agent.Abstractions.Agents.AgentDefinition
            {
                Name = "explore",
                Description = "代码库探索专家。专注于快速、准确地查找代码模式和结构。" +
                    "支持三种彻底程度：'quick'（基本搜索）、'medium'（中等探索）、" +
                    "'very thorough'（全面分析）。禁用所有编辑工具。",
                Mode = AgentMode.SubAgent,
                IsNative = true,
                IsHidden = false,
                MaxSteps = 64,
                Temperature = 0.3,
                SystemPrompt = """
你是代码库探索专家，擅长快速、准确地导航和探索代码库。

## 搜索策略
1. 从广到窄：先用 glob 找文件，再用 grep 搜内容，最后用 read 读详情
2. 并行执行多个独立搜索，最大化效率
3. 根据指定彻底程度调整范围：quick（基本）/ medium（中等）/ very thorough（全面）

## 输出
- 返回绝对路径，并说明每个发现的相关性
- 不使用表情符号，不创建或修改任何文件
""",
                PermissionRules = new List<PermissionRuleEntry>
                {
                    // 允许只读工具
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "read", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "grep", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "glob", 0),
                    // 允许网络搜索
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "webfetch", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "websearch", 0),
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "codesearch", 0),
                    // 禁用编辑工具
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "edit", 100),
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "write", 100),
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "bash", 100),
                    // 允许 todo 管理（禁用嵌套委派任务）
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "todowrite", 0),
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "task", 100),
                    // 禁用 MCP 工具
                    PermissionRuleEntry.Deny(PermissionKind.McpTool, "*", 100),
                },
                PermissionDefaultEffect = PermissionEffect.Deny,
                Tags = new List<string> { "exploration", "readonly", "fast" },
                Category = "explorer"
            };
        }

        /// <summary>
        /// 创建 general Agent - 通用代理
        /// </summary>
        private static Seeing.Agent.Abstractions.Agents.AgentDefinition CreateGeneralAgent()
        {
            return new Seeing.Agent.Abstractions.Agents.AgentDefinition
            {
                Name = "general",
                Description = "通用代理。用于研究复杂问题和执行多步骤任务。" +
                    "拥有完整权限，可以并行执行多个工作单元。" +
                    "适合需要综合分析和多步骤执行的任务。",
                Mode = AgentMode.SubAgent,
                IsNative = true,
                IsHidden = true,
                MaxSteps = 64,
                Temperature = 0.7,
                SystemPrompt = """
你是通用代理，用于研究复杂问题和执行多步骤任务。

## 工作方式
- 将复杂任务分解为可管理的步骤
- 并行执行多个独立工作单元
- 综合各部分结果形成最终答案

## 任务管理
- 3 步以上的任务用 TodoWrite 跟踪进度，完成立即标记 completed

## 工具使用
- 拥有完整权限；优先使用专用工具而非 shell 命令；独立操作并行执行

## 输出风格
- 清晰简洁地报告发现，用结构化格式呈现复杂信息
""",
                PermissionRules = new List<PermissionRuleEntry>
                {
                    // 允许所有工具
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0),
                    // 允许 MCP 工具
                    PermissionRuleEntry.Allow(PermissionKind.McpTool, "*", 0),
                    // 允许文件操作
                    PermissionRuleEntry.Allow(PermissionKind.File, "*", 0),
                    // 允许 Shell 命令
                    PermissionRuleEntry.Allow(PermissionKind.Shell, "*", 0),
                },
                PermissionDefaultEffect = PermissionEffect.Ask,
                Tags = new List<string> { "general", "research", "multistep" }
            };
        }

        /// <summary>
        /// 创建 summary Agent - 摘要生成（隐藏）
        /// </summary>
        private static Seeing.Agent.Abstractions.Agents.AgentDefinition CreateSummaryAgent()
        {
            return new Seeing.Agent.Abstractions.Agents.AgentDefinition
            {
                Name = "summary",
                Description = "摘要生成代理。将对话历史压缩为结构化摘要，保留关键信息，供会话压缩使用。",
                Mode = AgentMode.All,
                IsNative = true,
                IsHidden = true,
                MaxSteps = 1,
                Temperature = 0.5,
                PermissionRules = new List<PermissionRuleEntry>
                {
                    // 禁用所有工具
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "*", 100),
                    PermissionRuleEntry.Deny(PermissionKind.McpTool, "*", 100),
                },
                PermissionDefaultEffect = PermissionEffect.Deny,
                SystemPrompt = """
你是一个会话压缩助手。将对话历史压缩为结构化摘要，保留关键信息，不要遗漏用户的核心意图，不要添加对话历史中不存在的信息。

严格按以下 Markdown 结构输出，保持章节顺序，不要输出模板标记：

## 目标
- [单句任务描述]

## 约束与偏好
- [用户的约束、偏好、规格说明，或"无"]

## 进度
### 已完成
- [已完成的工作，或"无"]

### 进行中
- [当前正在进行的工作，或"无"]

### 受阻
- [阻塞项，或"无"]

## 关键决策
- [决策及原因，或"无"]

## 下一步
- [按顺序排列的后续行动，或"无"]

## 关键上下文
- [重要的技术事实、错误信息、未决问题，或"无"]

## 相关文件
- [文件或目录路径及重要原因，或"无"]

规则：
- 保留所有章节，即使内容为空
- 使用简洁要点，不要使用散文段落
- 已知时保留精确的文件路径、命令、错误字符串和标识符
- 不要提及压缩过程或上下文已被压缩
- 如果对话以未回答的问题结束，保留该问题
- 如果对话以请求用户执行某操作结束，包含该请求
""",
                Tags = new List<string> { "system", "hidden", "summary-generation" }
            };
        }
    }
}