using Microsoft.Extensions.Hosting;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Core.Hooks;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// 宿主启动时注册 ACP Session 生命周期 Hook（不依赖插件 InitializeAsync）。
/// </summary>
internal sealed class AcpHookRegistrationHostedService : IHostedService
{
    public const string ModuleId = "acp";

    private readonly HookManager _hookManager;
    private readonly AcpSessionLifecycleHook _lifecycleHook;
    private readonly IModuleCatalog? _catalog;
    private readonly AcpModuleActivity _activity;

    public AcpHookRegistrationHostedService(
        HookManager hookManager,
        AcpSessionLifecycleHook lifecycleHook,
        AcpModuleActivity activity,
        IModuleCatalog? catalog = null)
    {
        _hookManager = hookManager;
        _lifecycleHook = lifecycleHook;
        _activity = activity;
        _catalog = catalog;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
            return Task.CompletedTask;

        if (!_activity.IsActive && _catalog is not null)
            return Task.CompletedTask;

        _hookManager.RegisterMulti(_lifecycleHook);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
