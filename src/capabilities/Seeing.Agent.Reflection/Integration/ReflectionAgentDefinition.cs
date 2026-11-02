using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Integration;

/// <summary>
/// 复盘 Agent 的定义工厂。
/// <para>
/// 定义是纯数据，执行逻辑由 <c>AgentExecutor</c> 统一处理；本类只负责把
/// 「后台只读分析」的约束表达成 Agent 元数据。
/// </para>
/// </summary>
public static class ReflectionAgentDefinition
{
    /// <summary>复盘 Agent 名称（隐藏的内部代理）。</summary>
    public const string AgentName = "reflection";

    /// <summary>
    /// 构造复盘 Agent 定义。
    /// <para>
    /// 权限面刻意收窄到「能读、能写技能、不能 shell」：
    /// <c>read</c> 工具映射到 <see cref="PermissionKind.File"/>，因此这里**不**加
    /// <c>Deny(File, "*")</c>（那会把 read 一起拒掉）；写文件/执行命令的约束改由
    /// <see cref="AgentDefinition.DeniedTools"/> 与 <see cref="PermissionKind.Shell"/> 表达。
    /// </para>
    /// </summary>
    public static AgentDefinition Create(ReflectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AgentDefinition
        {
            Name = AgentName,
            Description = "后台复盘代理（内部）。只在任务收尾后判断是否有值得沉淀的经验。",

            // Primary + IsHidden 的组合是刻意的：
            // - Primary 表达「不可被 task 委派」——可委派列表按 Mode != Primary 排除；
            // - IsHidden 表达「不显示在用户选择列表」——用户选择列表按 !IsHidden 排除。
            // 两者缺一就会从其中一个列表漏出来；而 SubAgent + IsHidden 会污染可委派集合
            // （IsHidden 的契约语义是「隐藏但仍可作为 SubAgent 使用」）。
            Mode = AgentMode.Primary,
            IsHidden = true,

            IsNative = true,
            IsBackground = true,

            MaxSteps = options.MaxSteps,
            Temperature = 0.2,

            // 只读工具 + 技能读取 + 技能沉淀；写入类工具在 DeniedTools 中显式屏蔽。
            AllowedTools = ["read", "grep", "glob", "skill", "skill_create"],
            DeniedTools = ["task", "write", "edit", "delete"],

            PermissionRules =
            [
                PermissionRuleEntry.Allow(
                    PermissionKind.File,
                    "*",
                    priority: 0),
                PermissionRuleEntry.Deny(
                    PermissionKind.Shell,
                    "*",
                    priority: 100,
                    ns: null)
            ],

            // 复盘系统提示词：任务定义 + 反模式清单 + 写入纪律三段。
            // 技能清单由 SkillsContextSource 注入，提示词内不重复列举。
            SystemPrompt = Prompts.ReflectionSystem
        };
    }
}
