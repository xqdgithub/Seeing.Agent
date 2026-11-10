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
/// 宿主级「首次选定工作区之后」的补初始化。
/// <para>
/// 为什么需要它：未选定工作区时项目路径不可读，命令发现、TokenBudget 钩子、Gateway 渠道注册
/// 都会构造依赖项目路径的单例（会话存储 / 调度器），因此宿主启动阶段只能跳过。
/// 用户在工作区首页选定目录后，必须把这些补上，否则命令与钩子永久缺失。
/// </para>
/// <para>幂等：重复调用只执行一次。</para>
/// </summary>
public sealed class WorkspaceReadyInitializer
{
    private readonly IServiceProvider _services;
    private int _ran;

    public WorkspaceReadyInitializer(IServiceProvider services) => _services = services;

    /// <summary>执行补初始化（首个成功的工作区切换之后调用）。</summary>
    public void RunIfNeeded()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
            return;

        var workspace = _services.GetRequiredService<IWorkspaceProvider>();
        if (workspace.IsResolved)
            _services.ReloadGatewayChannelRegistry(workspace.GetProjectRoot());

        // UI 贡献不再需要在这里补登记：宿主壳层在 Build 后立即收集（与工作区无关），
        // 模块声明在结算后统一收集（见 WebUiHost / InitializeSeeingAsync）。

        _services.InitializeCommands();
        _services.InitializeAcpCommands();
        _services.UseTokenBudgetHooks();

        // 会话事件流路由 / 全局收件箱这类 UI 投影服务只依赖会话服务，按需解析不会崩
        _services.GetService<AppState>();
    }
}
