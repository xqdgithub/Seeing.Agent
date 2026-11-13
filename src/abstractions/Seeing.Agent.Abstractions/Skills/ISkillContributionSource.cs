namespace Seeing.Agent.Abstractions.Skills;

/// <summary>
/// 外部技能贡献源（如 MCP Skills 扩展）。只由 Skills 模块消费并进目录快照，禁止走 <see cref="ISkillCatalog.RegisterAsync"/>。
/// </summary>
public interface ISkillContributionSource
{
    /// <summary>稳定前缀，如 <c>mcp</c>。</summary>
    string SourceKind { get; }

    /// <summary>发现期列出贡献条目（仅元数据）。</summary>
    Task<IReadOnlyList<SkillContributionDescriptor>> ListAsync(CancellationToken ct = default);

    /// <summary>加载期按键回源取正文与附件；未知键返回 null。</summary>
    Task<SkillContributionContent?> LoadAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// 贡献源登记表：Skills 模块持有；MCP 等能力包只 <see cref="Register"/> / <see cref="Unregister"/>。
/// </summary>
public interface ISkillContributionRegistry
{
    /// <summary>登记一个贡献源（同实例重复登记忽略）。</summary>
    void Register(ISkillContributionSource source);

    /// <summary>撤销贡献源。</summary>
    void Unregister(ISkillContributionSource source);

    /// <summary>当前已登记的贡献源（只读快照）。</summary>
    IReadOnlyList<ISkillContributionSource> Sources { get; }

    /// <summary>
    /// 按键向各源回源加载；第一个返回非 null 的源胜出。
    /// </summary>
    Task<SkillContributionContent?> LoadAsync(string key, CancellationToken ct = default);
}
