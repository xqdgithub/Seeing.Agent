using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.BuiltInAgents
{
    /// <summary>
    /// 内置 Agent 定义 - 提供默认代理配置
    /// <para>
    /// 参考 opencode 的 Agent.Service 设计，提供：
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
        public static IEnumerable<AgentInfo> GetBuiltInAgents()
        {
            yield return CreateBuildAgent();
            yield return CreatePlanAgent();
            yield return CreateExploreAgent();
            yield return CreateGeneralAgent();
            yield return CreateTitleAgent();
            yield return CreateSummaryAgent();
        }

        /// <summary>
        /// 创建 build Agent - 默认主代理
        /// </summary>
        private static AgentInfo CreateBuildAgent()
        {
            return new AgentInfo
            {
                Name = "build",
                Description = "默认主代理。拥有完整权限，可执行所有工具（包括 MCP 工具、文件操作、Shell 命令）。" +
                    "适用于需要完整能力的任务，如代码编写、文件编辑、命令执行等。",
                Mode = AgentMode.All,
                IsNative = true,
                IsHidden = false,
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
        private static AgentInfo CreatePlanAgent()
        {
            return new AgentInfo
            {
                Name = "plan",
                Description = "计划模式代理。专注于分析和规划，只允许读取操作和计划文件管理。" +
                    "允许使用 skill 加载技能、todowrite 管理任务列表、task 委托子代理。" +
                    "文件写入仅限于 .plans/ 目录和项目根目录下的 .md 文件（计划文档）。" +
                    "禁用 Shell 命令、代码编辑和 MCP 工具。",
                Mode = AgentMode.All,
                IsNative = true,
                IsHidden = false,
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
        private static AgentInfo CreateExploreAgent()
        {
            return new AgentInfo
            {
                Name = "explore",
                Description = "代码库探索专家。专注于快速、准确地查找代码模式和结构。" +
                    "支持三种彻底程度：'quick'（基本搜索）、'medium'（中等探索）、" +
                    "'very thorough'（全面分析）。禁用所有编辑工具。",
                Mode = AgentMode.SubAgent,
                IsNative = true,
                IsHidden = false,
                SystemPrompt = "你是一个代码库探索专家。专注于快速、准确地查找代码模式和结构。" +
                    "使用 grep、glob、read 等工具进行代码搜索和分析。" +
                    "不要尝试修改任何文件，只进行读取和分析。",
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
                    // 禁用任务和 todo
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "task", 100),
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "todowrite", 100),
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
        private static AgentInfo CreateGeneralAgent()
        {
            return new AgentInfo
            {
                Name = "general",
                Description = "通用代理。用于研究复杂问题和执行多步骤任务。" +
                    "拥有完整权限，可以并行执行多个工作单元。" +
                    "适合需要综合分析和多步骤执行的任务。",
                Mode = AgentMode.SubAgent,
                IsNative = true,
                IsHidden = true,
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
        /// 创建 title Agent - 标题生成（隐藏）
        /// </summary>
        private static AgentInfo CreateTitleAgent()
        {
            return new AgentInfo
            {
                Name = "title",
                Description = "标题生成代理。为对话生成简洁、准确的标题。",
                Mode = AgentMode.All,
                IsNative = true,
                IsHidden = true,
                Temperature = 0.5,
                PermissionRules = new List<PermissionRuleEntry>
                {
                    // 禁用所有工具
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "*", 100),
                    PermissionRuleEntry.Deny(PermissionKind.McpTool, "*", 100),
                },
                PermissionDefaultEffect = PermissionEffect.Deny,
                SystemPrompt = "为对话生成简洁、准确的标题。标题应反映对话的主要主题或任务。"
            };
        }

        /// <summary>
        /// 创建 summary Agent - 摘要生成（隐藏）
        /// </summary>
        private static AgentInfo CreateSummaryAgent()
        {
            return new AgentInfo
            {
                Name = "summary",
                Description = "摘要生成代理。为对话生成简洁摘要，保留关键信息。",
                Mode = AgentMode.All,
                IsNative = true,
                IsHidden = true,
                PermissionRules = new List<PermissionRuleEntry>
                {
                    // 禁用所有工具
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "*", 100),
                    PermissionRuleEntry.Deny(PermissionKind.McpTool, "*", 100),
                },
                PermissionDefaultEffect = PermissionEffect.Deny,
                SystemPrompt = "生成对话的简洁摘要，保留关键信息、决策和结果。"
            };
        }
    }
}