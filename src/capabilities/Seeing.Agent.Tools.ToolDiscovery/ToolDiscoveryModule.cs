using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.ToolDiscovery;

/// <summary>
/// 工具检索模块 — 提供 tool_search（延迟暴露目录的检索与按需展开）。
/// <para>只依赖 Abstractions + Seeing.Session + Tools.Support；不引用 Core。</para>
/// </summary>
public sealed class ToolDiscoveryModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools = [ToolIdConventions.SearchToolId];

    /// <inheritdoc />
    public string Id => "tool-discovery";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ToolSearchTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        // tool_search 依赖结算端口（Hosting）与发现态写入端口；缺任一则无法工作。
        // 与 SessionToolsModule 对齐：缺依赖时 Warning 跳过，不因单个能力缺宿主而中断整机启动。
        if (services.GetService<ISessionToolSchemaResolver>() is null)
        {
            services.GetService<ILogger<ToolDiscoveryModule>>()?.LogWarning(
                "ISessionToolSchemaResolver 未注册，tool_search 未启用（需 Hosting 结算端口）");
            return;
        }

        await tm.RegisterToolAsync(
            services.GetRequiredService<ToolSearchTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
