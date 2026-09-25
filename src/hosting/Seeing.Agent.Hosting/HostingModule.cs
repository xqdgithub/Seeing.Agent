using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Tools;

namespace Seeing.Agent.Hosting;

/// <summary>
/// Hosting 子代理/Todo 工具模块 — id=<c>subagent</c>；提供 task/task_status/todowrite。
/// </summary>
public sealed class HostingModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
        ["task", "task_status", "todowrite"];

    /// <inheritdoc />
    public string Id => "subagent";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 工具具体类型由 AddChatOrchestrator 登记。
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        if (services.GetService<TaskTool>() is { } task)
        {
            // ITool.Description 为同步契约，注册前异步预热描述缓存（含可委托 Agent 列表）
            await task.WarmDescriptionAsync(cancellationToken).ConfigureAwait(false);
            await tm.RegisterToolAsync(task, cancellationToken).ConfigureAwait(false);
        }
        if (services.GetService<TaskStatusTool>() is { } status)
            await tm.RegisterToolAsync(status, cancellationToken).ConfigureAwait(false);
        if (services.GetService<TodoWriteTool>() is { } todo)
            await tm.RegisterToolAsync(todo, cancellationToken).ConfigureAwait(false);
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
