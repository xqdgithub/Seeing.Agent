using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Agent.Memory.Integration.Tools;

namespace Seeing.Agent.Memory;

/// <summary>
/// Memory 能力模块 — id=<c>memory</c>；Sqlite 连接由 Activate/Deactivate 经 <see cref="SqliteConnectionOwner"/> 自管。
/// </summary>
public sealed class MemoryModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedTools =
        ["memory_search", "memory_write", "memory_read"];

    private readonly MemoryModuleActivity? _activity;
    private readonly SqliteConnectionOwner? _connectionOwner;
    private readonly IUiContributionRegistry? _ui;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public MemoryModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public MemoryModule(
        MemoryModuleActivity activity,
        SqliteConnectionOwner connectionOwner,
        IUiContributionRegistry? uiRegistry = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _connectionOwner = connectionOwner ?? throw new ArgumentNullException(nameof(connectionOwner));
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "memory";

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 实际 DI 登记由 AddMemoryServices 完成。
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/memory", "记忆", "database", ["memory"],
            Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 50),
        new NavContribution("/memory/settings", "记忆设置", "setting", ["memory"],
            Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 51),
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null || _connectionOwner is null)
        {
            throw new InvalidOperationException(
                "MemoryModule.ActivateAsync requires DI-resolved MemoryModule (activity + connection owner).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _connectionOwner.Open();
        _activity.MarkActive();
        _ui?.Register(this);

        // Hook 与本模块生命周期绑定：Activate 登记、Deactivate 撤销，单一真相源为模块启停。
        RegisterHooks(services);

        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        if (services.GetService<MemorySearchTool>() is { } search)
            await tm.RegisterToolAsync(search, cancellationToken).ConfigureAwait(false);
        if (services.GetService<MemoryWriteTool>() is { } write)
            await tm.RegisterToolAsync(write, cancellationToken).ConfigureAwait(false);
        if (services.GetService<MemoryReadTool>() is { } read)
            await tm.RegisterToolAsync(read, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        UnregisterHooks(services);

        var tm = services.GetService<IToolManager>();
        if (tm is not null)
        {
            foreach (var id in ProvidedTools)
                await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
        }

        _ui?.Unregister(Id);

        if (_activity is null || _connectionOwner is null)
            return;

        _activity.MarkInactiveAndWake();
        _connectionOwner.Close();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void RegisterHooks(IServiceProvider services)
    {
        if (services.GetService<IHookManager>() is not { } hooks)
            return;

        if (services.GetService<ChatMemoryHandler>() is { } chat)
            hooks.Register(chat);
        if (services.GetService<ToolMemoryHandler>() is { } tool)
            hooks.Register(tool);
        if (services.GetService<AgentTurnMemoryHandler>() is { } turn)
            hooks.Register(turn);
        if (services.GetService<MemoryRecallHandler>() is { } recall)
            hooks.Register(recall);
    }

    private static void UnregisterHooks(IServiceProvider services)
    {
        if (services.GetService<IHookManager>() is not { } hooks)
            return;

        if (services.GetService<ChatMemoryHandler>() is { } chat)
            hooks.Remove(chat);
        if (services.GetService<ToolMemoryHandler>() is { } tool)
            hooks.Remove(tool);
        if (services.GetService<AgentTurnMemoryHandler>() is { } turn)
            hooks.Remove(turn);
        if (services.GetService<MemoryRecallHandler>() is { } recall)
            hooks.Remove(recall);
    }
}
