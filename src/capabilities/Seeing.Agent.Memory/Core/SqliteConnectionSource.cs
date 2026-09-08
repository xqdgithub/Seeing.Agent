using Microsoft.Data.Sqlite;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 共享 Sqlite 连接解析：生产路径经 <see cref="SqliteConnectionOwner.RequireOpen"/> 每次取用；
/// 测试可注入已打开的 <see cref="SqliteConnection"/>。
/// </summary>
public sealed class SqliteConnectionSource
{
    private readonly SqliteConnectionOwner? _owner;
    private readonly SqliteConnection? _direct;

    public SqliteConnectionSource(SqliteConnectionOwner owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public SqliteConnectionSource(SqliteConnection connection)
    {
        _direct = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>获取当前可用连接（模块未 Activate 时抛错）。</summary>
    public SqliteConnection Get() =>
        _owner?.RequireOpen()
        ?? _direct
        ?? throw new InvalidOperationException("Sqlite connection source is empty.");
}
