using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Hosting;
using Seeing.Agent.TokenBudget.Extensions;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Hosting;

/// <summary>
/// 宿主级「首次选定工作区之后」的<b>进程级一次性</b>补初始化。
/// <para>
/// 为什么需要它：未选定工作区时项目路径不可读，命令发现、TokenBudget 钩子、Gateway 渠道注册
/// 都会构造依赖项目路径的单例（会话存储 / 调度器），因此宿主启动阶段只能跳过。
/// 用户在工作区首页选定目录后，必须把这些补上，否则命令与钩子永久缺失。
/// </para>
/// <para><b>幂等（进程级只执行一次）</b>：重复调用只执行一次。</para>
/// <para>
/// <b>禁止把「每次切换工作区都要重做」的逻辑加到这里</b>——它被 <c>_ran</c> 锁成一次性，
/// 第二次切换会静默不执行。这类工作必须注册为 <see cref="IReloadHandler"/>（工作区变更时由
/// 重载编排器统一调用），例如：
/// 组件重扫 <c>ComponentManager</c>、技能命令 <c>DynamicSkillCommandSync</c>、
/// Markdown 命令 <c>MarkdownCommandLoader</c>、Gateway 渠道 <c>GatewayChannelRegistryReloadHandler</c>。
/// 本类只保留「进程级装配」与「首个工作区的 bootstrap」。
/// </para>
/// </summary>
public sealed class WorkspaceReadyInitializer
{
    private readonly IServiceProvider _services;
    private int _ran;

    public WorkspaceReadyInitializer(IServiceProvider services) => _services = services;

    /// <summary>执行补初始化（首个成功的工作区切换之后调用；进程级只做一次）。</summary>
    public void RunIfNeeded()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
            return;

        // 首个工作区的 bootstrap：渠道注册表按根加载一次。
        // 后续每一次切换由 GatewayChannelRegistryReloadHandler（WorkspaceChange）负责，不依赖本方法。
        var workspace = _services.GetRequiredService<IWorkspaceProvider>();
        if (workspace.IsResolved)
            _services.ReloadGatewayChannelRegistry(workspace.GetProjectRoot());

        // UI 贡献不再需要在这里补登记：宿主壳层在 Build 后立即收集（与工作区无关），
        // 模块声明在结算后统一收集（见 WebUiHost / InitializeSeeingAsync）。

        // 命令发现：内含内置命令 + 模块静态命令 + 用户/项目级 Markdown 命令。
        // 切换工作区后的 Markdown 命令重扫走 MarkdownCommandLoader（IReloadHandler），不在这里。
        _services.InitializeCommands();
        _services.InitializeAcpCommands();

        // TokenBudget 钩子：进程级装配，只挂一次
        _services.UseTokenBudgetHooks();

        // 会话事件流路由 / 全局收件箱这类 UI 投影服务只依赖会话服务，按需解析不会崩
        _services.GetService<AppState>();
    }
}
