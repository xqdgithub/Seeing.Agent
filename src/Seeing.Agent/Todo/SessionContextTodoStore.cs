using Seeing.Agent.Abstractions.Todo;
using Seeing.Session.Core;

namespace Seeing.Agent.Todo;

/// <summary>
/// 基于 Session Context 的 Todo 存储实现（默认适配器）。
/// <para>
/// 将 Todo 列表桥接进会话内存 Context，魔法键 "todos" 封装在适配器内部，
/// 不再泄漏到领域层（端口-适配器模式）。
/// </para>
/// </summary>
public sealed class SessionContextTodoStore : ITodoStore
{
    private const string TodoContextKey = "todos";
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// 创建 SessionContextTodoStore 实例
    /// </summary>
    /// <param name="sessionManager">会话管理器</param>
    public SessionContextTodoStore(ISessionManager sessionManager)
        => _sessionManager = sessionManager;

    /// <inheritdoc/>
    public Task<TodoList> LoadAsync(string sessionId)
    {
        var session = _sessionManager.Get(sessionId);
        var items = session?.GetContext<List<TodoItem>>(TodoContextKey) ?? new List<TodoItem>();
        return Task.FromResult(new TodoList { SessionId = sessionId, Items = items });
    }

    /// <inheritdoc/>
    public Task SaveAsync(string sessionId, TodoList todos)
    {
        var session = _sessionManager.Get(sessionId);
        if (session == null)
            throw new InvalidOperationException($"会话不存在: {sessionId}");

        session.SetContext(TodoContextKey, todos.Items);
        return Task.CompletedTask;
    }
}