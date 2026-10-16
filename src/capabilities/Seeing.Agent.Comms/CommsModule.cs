using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Comms;

/// <summary>
/// 通联模块。DI 由 <c>AddSeeingComms</c> 完成；Activate 留空，工具挂载在后续任务。
/// </summary>
public sealed class CommsModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "comms";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = [];

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
