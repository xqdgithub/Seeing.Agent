using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Hosting.Execution;

namespace Seeing.Agent.Hosting;

/// <summary>
/// "一个可执行宿主的最小集" 的统一注册入口。
/// <para>
/// <b>为什么需要它：</b>五个 Host Shape 对「最小可执行集」的定义此前不一致——
/// Tui / Headless / Embed 注册 <c>AddSessionHosting + AddExecutionEngine</c>，而
/// <c>Hosting.Web</c> 只有 Circuit 壳 + 权限通道、<c>Hosting.Gateway</c> 只有形态描述。
/// 结果 sample 层出现第二、第三份执行引擎配置手抄：
/// <c>WebUiHost</c>、<c>Gateway.Server/Program.cs</c> 各自补齐参数完全相同的两行。
/// 新增执行选项（如 ExecutionHistoryLimit）就要在多处同步，漏改表现为解析 ISessionRuntime 失败。
/// </para>
/// <para>
/// 各 Shape 包的 <c>AddSeeingHosting*</c> 都应调用本方法，再叠加自己的界面表面
/// （Circuit/终端/无界面/嵌入式）与权限通道实现。
/// </para>
/// </summary>
public static class HostShapeRegistration
{
    /// <summary>HostDefaultSeams 的缺省缝：执行世界固定为本地 IO。</summary>
    private static readonly IReadOnlyDictionary<string, string> s_defaultSeams =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["executionWorld"] = "io.local",
        };

    /// <summary>
    /// 注册形态无关的宿主基线：形态描述 + 进程结算选项 + 会话运行时 + 执行引擎，
    /// 并按需注册共享的空权限通道。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="descriptor">本形态的描述符。</param>
    /// <param name="configureExecution">可选执行引擎配置（历史会话上限等）。</param>
    /// <param name="withPermissionChannel">
    /// 是否注册共享的「事件流承载」权限通道。交互式形态（Web/Tui）需要；
    /// 无交互形态（Headless/Embed/Gateway）应注册 <c>DenyAllPermissionChannel</c> 而非本通道，故传 false。
    /// </param>
    /// <param name="defaultBoot">可选的进程级默认 Boot（如 Embed 用 <c>minimal</c>）；null 表示不覆盖。</param>
    public static IServiceCollection AddHostShapeBaseline(
        this IServiceCollection services,
        HostShapeDescriptor descriptor,
        Action<ExecutionOptions>? configureExecution = null,
        bool withPermissionChannel = true,
        string? defaultBoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddSingleton(descriptor);

        // HostDefaultSeams 至少 executionWorld → io.local；Boot 未设时由结算回退 "*"
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultScenario = descriptor.DefaultScenario,
            HostDefaultBoot = defaultBoot,
            HostDefaultSeams = new Dictionary<string, string>(s_defaultSeams, StringComparer.OrdinalIgnoreCase),
            BootOverride = BootOverrideSource.ResolveFromEnvironment(),
        });

        if (withPermissionChannel)
            services.TryAddSingleton<IPermissionChannel, EventStreamPermissionChannel>();

        services.AddSessionHosting();
        services.AddExecutionEngine(configureExecution);

        // 工作区切换：所有宿主形态共用同一条 Apply 路径
        services.TryAddSingleton<IWorkspaceConfigReloader, WorkspaceConfigReloader>();
        services.TryAddSingleton<IWorkspaceReloadInvoker, WorkspaceReloadInvoker>();
        services.TryAddSingleton<IWorkspaceSwitcher, WorkspaceSwitcher>();

        return services;
    }
}
