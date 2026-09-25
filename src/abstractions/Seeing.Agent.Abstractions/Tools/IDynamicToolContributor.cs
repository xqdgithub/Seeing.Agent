namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 动态工具贡献契约 — 模块在 Activate 时登记、Deactivate 时撤销。
/// <para>
/// 与 <see cref="Modules.ISeeingModule.ProvidedTools"/>（静态清单）互补：
/// 工具集在运行时可变（如 MCP Server 连接/断开动态增删工具），
/// 由本契约在结算时把动态工具 id 并入 settledToolIds。
/// </para>
/// </summary>
public interface IDynamicToolContributor
{
    /// <summary>归属模块 id（与 <see cref="Modules.ISeeingModule.Id"/> 一致）。</summary>
    string ModuleId { get; }

    /// <summary>当前贡献的动态工具 id 快照（可随运行时连接/断开变化）。</summary>
    IReadOnlyCollection<string> GetDynamicToolIds();
}

/// <summary>
/// 动态工具贡献注册表 — 结算时按启用模块集合并入其动态工具 id。
/// </summary>
public interface IDynamicToolContributorRegistry
{
    /// <summary>登记动态工具贡献者（同 <see cref="IDynamicToolContributor.ModuleId"/> 覆盖）。</summary>
    void Register(IDynamicToolContributor contributor);

    /// <summary>撤销指定模块的动态工具贡献。</summary>
    void Unregister(string moduleId);

    /// <summary>取指定启用模块集合的动态工具 id 并集（去重、稳定排序）。</summary>
    IReadOnlyCollection<string> GetDynamicToolIds(IEnumerable<string> enabledModuleIds);
}
