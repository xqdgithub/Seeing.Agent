using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.BuiltInAgents
{
    /// <summary>
    /// 内置 Agent 定义 - 提供默认代理配置
    /// <para>
    /// 参考 opencode 的 Agent.Service 设计，提供：
    /// - build: 默认主代理，执行工具
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
                Description = "默认代理。根据配置的权限执行工具。",
                Mode = AgentMode.All,
                IsNative = true,
                IsHidden = false,
                Permissions = new List<PermissionRule>
                {
                    // 默认权限：允许所有
                    new PermissionRule { Permission = "*", Pattern = "*", Action = PermissionAction.Allow },
                    // doom_loop 需要询问
                    new PermissionRule { Permission = "doom_loop", Pattern = "*", Action = PermissionAction.Ask },
                    // 允许问题工具
                    new PermissionRule { Permission = "question", Pattern = "*", Action = PermissionAction.Allow },
                    // 允许进入计划模式
                    new PermissionRule { Permission = "plan_enter", Pattern = "*", Action = PermissionAction.Allow },
                }
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
                Description = "计划模式。禁用所有编辑工具。",
                Mode = AgentMode.Primary,
                IsNative = true,
                IsHidden = false,
                Permissions = new List<PermissionRule>
                {
                    new PermissionRule { Permission = "*", Pattern = "*", Action = PermissionAction.Allow },
                    new PermissionRule { Permission = "doom_loop", Pattern = "*", Action = PermissionAction.Ask },
                    // 允许退出计划模式
                    new PermissionRule { Permission = "plan_exit", Pattern = "*", Action = PermissionAction.Allow },
                    // 允许问题工具
                    new PermissionRule { Permission = "question", Pattern = "*", Action = PermissionAction.Allow },
                    // 禁用编辑工具（除了计划文件）
                    new PermissionRule { Permission = "edit", Pattern = "*", Action = PermissionAction.Deny },
                    new PermissionRule { Permission = "write", Pattern = "*", Action = PermissionAction.Deny },
                },
                Tags = new List<string> { "planning", "readonly" }
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
                Description = "快速代理，专门用于探索代码库。" +
                    "当需要快速按模式查找文件（如 'src/components/**/*.tsx'）、" +
                    "搜索代码关键字（如 'API endpoints'）、" +
                    "或回答关于代码库的问题时使用。" +
                    "调用此代理时，请指定所需的彻底程度：" +
                    "'quick' 用于基本搜索，'medium' 用于中等探索，" +
                    "'very thorough' 用于跨多个位置和命名约定的全面分析。",
                Mode = AgentMode.SubAgent,
                IsNative = true,
                IsHidden = false,
                SystemPrompt = "你是一个代码库探索专家。专注于快速、准确地查找代码模式和结构。",
                Permissions = new List<PermissionRule>
                {
                    // 允许只读工具
                    new PermissionRule { Permission = "grep", Pattern = "*", Action = PermissionAction.Allow },
                    new PermissionRule { Permission = "glob", Pattern = "*", Action = PermissionAction.Allow },
                    new PermissionRule { Permission = "read", Pattern = "*", Action = PermissionAction.Allow },
                    new PermissionRule { Permission = "bash", Pattern = "*", Action = PermissionAction.Allow },
                    // 允许网络搜索
                    new PermissionRule { Permission = "webfetch", Pattern = "*", Action = PermissionAction.Allow },
                    new PermissionRule { Permission = "websearch", Pattern = "*", Action = PermissionAction.Allow },
                    new PermissionRule { Permission = "codesearch", Pattern = "*", Action = PermissionAction.Allow },
                    // 禁用编辑工具
                    new PermissionRule { Permission = "edit", Pattern = "*", Action = PermissionAction.Deny },
                    new PermissionRule { Permission = "write", Pattern = "*", Action = PermissionAction.Deny },
                    // 禁用任务和 todo
                    new PermissionRule { Permission = "task", Pattern = "*", Action = PermissionAction.Deny },
                    new PermissionRule { Permission = "todowrite", Pattern = "*", Action = PermissionAction.Deny },
                },
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
                Description = "通用代理，用于研究复杂问题和执行多步骤任务。" +
                    "使用此代理并行执行多个工作单元。",
                Mode = AgentMode.SubAgent,
                IsNative = true,
                IsHidden = false,
                Permissions = new List<PermissionRule>
                {
                    new PermissionRule { Permission = "*", Pattern = "*", Action = PermissionAction.Allow },
                    new PermissionRule { Permission = "doom_loop", Pattern = "*", Action = PermissionAction.Ask },
                    // 禁用 todo 工具
                    new PermissionRule { Permission = "todowrite", Pattern = "*", Action = PermissionAction.Deny },
                },
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
                Description = "生成会话标题",
                Mode = AgentMode.Primary,
                IsNative = true,
                IsHidden = true,
                Temperature = 0.5,
                Permissions = new List<PermissionRule>
                {
                    // 禁用所有工具
                    new PermissionRule { Permission = "*", Pattern = "*", Action = PermissionAction.Deny },
                },
                SystemPrompt = "为对话生成简洁的标题。"
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
                Description = "生成对话摘要",
                Mode = AgentMode.Primary,
                IsNative = true,
                IsHidden = true,
                Permissions = new List<PermissionRule>
                {
                    // 禁用所有工具
                    new PermissionRule { Permission = "*", Pattern = "*", Action = PermissionAction.Deny },
                },
                SystemPrompt = "生成对话的简洁摘要，保留关键信息。"
            };
        }
    }
}