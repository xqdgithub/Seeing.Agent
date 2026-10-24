using Seeing.Agent.Abstractions.Context;

namespace Seeing.Agent.Abstractions.Skills;

/// <summary>
/// 执行级技能目录钉住端口：一次执行持有进入时的快照，执行期间不随目录变化而漂移。
/// <para>
/// <b>本接口是 <see cref="IContextExecutionScope"/> 的语义别名</b>（不额外声明成员）：
/// 技能快照是执行级事实之一，与 cwd / 会话同属一个作用域实例，因此由同一个 scoped 实现承载，
/// 不另建「只装快照」的第二个对象（避免两份真相、避免单例捕获 scoped 实例）。
/// </para>
/// <para>读取快照请用 <see cref="IContextExecutionScope.SkillSnapshot"/>（可空，宿主未组合技能模块时为 null）。</para>
/// </summary>
public interface ISkillCatalogPin : IContextExecutionScope
{
}
