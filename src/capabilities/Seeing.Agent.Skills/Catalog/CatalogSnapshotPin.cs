using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// 宿主兜底钉住实现：快照即目录当前快照。
/// <para>执行级钉住由宿主（ExecutionRunner）在每次执行作用域注册更精确的实现；
/// 本类只在宿主未提供 <see cref="ISkillCatalogPin"/> 时保证技能源仍可解析，不产生第二份真相。</para>
/// </summary>
internal sealed class CatalogSnapshotPin(ISkillCatalog catalog) : ISkillCatalogPin
{
    /// <inheritdoc />
    public SkillCatalogSnapshot Snapshot => catalog.Current;
}
