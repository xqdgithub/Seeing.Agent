namespace Seeing.Agent.Abstractions.Skills;

/// <summary>
/// 执行级技能目录钉住端口：一次执行持有进入时的快照，执行期间不随目录变化而漂移。
/// </summary>
public interface ISkillCatalogPin
{
    /// <summary>本次执行期间固定使用的快照。</summary>
    SkillCatalogSnapshot Snapshot { get; }
}
