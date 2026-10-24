using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Abstractions.Context;

/// <summary>
/// 执行级上下文事实：一次执行（一轮）内的会话、扫描根与钉住快照。
/// <para>
/// 由宿主（<c>ExecutionRunner</c> / Scheduler 旁路）在每次执行开始、**装配之前**填充一次；
/// 上下文源只读取，不得改写。执行结束后随作用域一并释放，绝不跨执行复用。
/// </para>
/// <para>
/// 生命周期纪律：本端口必须是 <b>scoped</b>（与装配器同作用域）。单例源若捕获它，
/// 就会钉住某个已释放的执行事实——这正是「钉住静默失效」的根因，禁止如此接线。
/// </para>
/// </summary>
public interface IContextExecutionScope
{
    /// <summary>本轮执行所属会话 ID。</summary>
    string SessionId { get; set; }

    /// <summary>本轮执行的工作目录（会话 cwd，不是进程当前目录）。</summary>
    string Cwd { get; set; }

    /// <summary>本轮执行的工作区根（项目配置归属根）。</summary>
    string WorkspaceRoot { get; set; }

    /// <summary>
    /// 本轮执行钉住的技能目录快照：装配器与工具必须看到同一个对象，执行期间不随目录变化漂移。
    /// 宿主未组合技能模块时为 null（技能源按不可用处理，保留上次已叙述内容）。
    /// </summary>
    SkillCatalogSnapshot? SkillSnapshot { get; set; }
}
