using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Workbench.Sources;
using Seeing.Agent.Workbench.Tools;

namespace Seeing.Agent.Workbench;

/// <summary>
/// 工作台能力模块（模块 id <c>workbench</c>）。
/// <para>
/// <b>它不登记任何工作台</b>：工作台由提供它的能力模块自己 <c>DescribeUi()</c> 声明
/// （与工具在 <c>Activate</c> 挂载同一条纪律——谁拥有谁声明）。本模块只提供三样东西：
/// ① 让 agent 能<b>列目录</b>与<b>打开</b>工作台的两个工具；
/// ② UI 推送快照的存储（<see cref="IWorkbenchSessionStateStore"/>）；
/// ③ 把快照回流成上下文源的 <see cref="WorkbenchContextSource"/>；
/// ④ 给插件工具用的 <see cref="IWorkbenchClient"/> 实现。
/// </para>
/// <para>依赖 <c>ui.registry</c>？不——信封注册表由 Core 无条件注册，本模块只读它。</para>
/// </summary>
public sealed class WorkbenchModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools = ["open_workbench", "list_workbenches"];

    /// <inheritdoc />
    public string Id => "workbench";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 快照存储：UI 写、服务端读。Singleton（跨 circuit 汇总；后写覆盖是多标签的已知边界）
        services.TryAddSingleton<IWorkbenchSessionStateStore, WorkbenchSessionStateStore>();

        // 插件调用自己 UI 实例的端口（能力包只依赖契约，实现住在实现包）
        services.TryAddSingleton<IWorkbenchClient, WorkbenchClient>();

        services.TryAddSingleton<OpenWorkbenchTool>();
        services.TryAddSingleton<ListWorkbenchesTool>();

        // 上下文源必须 scoped（与执行作用域同寿）：单例源会捕获某次执行的事实
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, WorkbenchContextSource>());
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tools = services.GetService<IToolManager>();
        if (tools is null)
            return;

        if (services.GetService<OpenWorkbenchTool>() is { } open)
            await tools.RegisterToolAsync(open, cancellationToken).ConfigureAwait(false);

        if (services.GetService<ListWorkbenchesTool>() is { } list)
            await tools.RegisterToolAsync(list, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tools = services.GetService<IToolManager>();
        if (tools is null)
            return;

        foreach (var id in ProvidedTools)
            await tools.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
