using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Abstractions.Modules;

/// <summary>
/// Seeing 能力模块契约 — DI 登记与激活/停用生命周期
/// </summary>
public interface ISeeingModule
{
    /// <summary>稳定模块 id（如 filesystem、mcp、llm.openai）</summary>
    string Id { get; }

    /// <summary>本模块提供的工具 id 列表</summary>
    IReadOnlyList<string> ProvidedTools { get; }

    /// <summary>本模块提供的 seam id 列表</summary>
    IReadOnlyList<string> ProvidedSeams { get; }

    /// <summary>依赖的其他模块 id</summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>登记 DI 服务（禁止在此无条件启动连接或 HostedService）</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>激活：注册工具、连接外部资源、登记 ReloadHandler 等</summary>
    Task ActivateAsync(CancellationToken cancellationToken = default);

    /// <summary>停用：对称卸载工具、断开连接、撤销 Handler</summary>
    Task DeactivateAsync(CancellationToken cancellationToken = default);
}
