using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Core.Context;

/// <summary>
/// 执行级上下文事实的实现：由宿主每次执行创建一个作用域实例并填充一次。
/// <para>
/// 填充时机固定为「本执行作用域创建之后、上下文装配之前」：属性在本轮内随后只读，
/// 装配器与各源看到的是同一份事实（含同一个技能快照对象）。
/// </para>
/// <para>
/// <b>必须注册为 scoped</b>：把它注册成单例会让某一次执行的事实泄漏到其它会话/后续执行，
/// 钉住与扫描根都会静默失真。
/// </para>
/// </summary>
public sealed class ContextExecutionScope : IContextExecutionScope, ISkillCatalogPin
{
    /// <inheritdoc />
    public string SessionId { get; set; } = string.Empty;

    /// <inheritdoc />
    public string Cwd { get; set; } = string.Empty;

    /// <inheritdoc />
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <inheritdoc />
    public SkillCatalogSnapshot? SkillSnapshot { get; set; }
}
