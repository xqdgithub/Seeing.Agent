using System.Data;
using Microsoft.Data.Sqlite;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// Memory Sqlite 连接的模块级所有者 — DI 仅登记空壳；
/// <see cref="Open"/> / <see cref="Close"/> 由模块 Activate / Deactivate 调用。
/// 消费者应持有本 Owner（经 <see cref="SqliteConnectionSource"/>），在每次操作时
/// <see cref="RequireOpen"/>，勿在构造时捕获 <see cref="SqliteConnection"/>。
/// </summary>
public sealed class SqliteConnectionOwner : IDisposable
{
    private readonly Func<string> _connectionStringFactory;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteConnectionOwner(Func<string> connectionStringFactory)
    {
        _connectionStringFactory = connectionStringFactory
            ?? throw new ArgumentNullException(nameof(connectionStringFactory));
    }

    /// <summary>连接是否处于 Open 状态。</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _connection?.State == ConnectionState.Open;
        }
    }

    /// <summary>
    /// 获取（必要时惰性创建、未 Open）的共享连接实例。
    /// 仅供诊断 / 测试；业务读写请用 <see cref="RequireOpen"/>。
    /// </summary>
    public SqliteConnection EnsureInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            return _connection ??= new SqliteConnection(_connectionStringFactory());
        }
    }

    /// <summary>
    /// 返回已 Open 的共享连接；模块未 Activate 时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public SqliteConnection RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_connection is null || _connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException(
                    "Memory Sqlite connection is not open; activate the memory module before use.");
            }

            return _connection;
        }
    }

    /// <summary>Activate：确保实例存在并 Open。</summary>
    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _connection ??= new SqliteConnection(_connectionStringFactory());
            if (_connection.State != ConnectionState.Open)
                _connection.Open();
        }
    }

    /// <summary>Deactivate：Close（保留实例以便再 Activate 重开）。</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_connection is { State: ConnectionState.Open })
                _connection.Close();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
            _disposed = true;
        }
    }
}
