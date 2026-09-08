using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Integration.Hosting;

namespace Seeing.Agent.Memory;

/// <summary>
/// Memory 能力模块 — id=<c>memory</c>；Sqlite 连接由 Activate/Deactivate 自管开关。
/// </summary>
public sealed class MemoryModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
        ["memory_search", "memory_write", "memory_read"];

    private readonly MemoryModuleActivity? _activity;
    private readonly Func<SqliteConnection>? _connectionFactory;
    private readonly SqliteConnection? _connection;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public MemoryModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public MemoryModule(
        MemoryModuleActivity activity,
        Func<SqliteConnection> connectionFactory,
        SqliteConnection connection)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc />
    public string Id => "memory";

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
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activity is null || _connectionFactory is null || _connection is null)
        {
            throw new InvalidOperationException(
                "MemoryModule.ActivateAsync requires DI-resolved MemoryModule (activity + connection factory).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = _connectionFactory;
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        _activity.MarkActive();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activity is null)
            return Task.CompletedTask;

        _activity.MarkInactiveAndWake();
        if (_connection is { State: ConnectionState.Open })
            _connection.Close();
        return Task.CompletedTask;
    }
}
