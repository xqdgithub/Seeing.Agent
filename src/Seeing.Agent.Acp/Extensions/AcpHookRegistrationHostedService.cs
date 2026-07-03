using Microsoft.Extensions.Hosting;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Core.Hooks;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// 宿主启动时注册 ACP Session 生命周期 Hook（不依赖插件 InitializeAsync）。
/// </summary>
internal sealed class AcpHookRegistrationHostedService : IHostedService
{
    private readonly HookManager _hookManager;
    private readonly AcpSessionLifecycleHook _lifecycleHook;

    public AcpHookRegistrationHostedService(
        HookManager hookManager,
        AcpSessionLifecycleHook lifecycleHook)
    {
        _hookManager = hookManager;
        _lifecycleHook = lifecycleHook;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hookManager.RegisterMulti(_lifecycleHook);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
