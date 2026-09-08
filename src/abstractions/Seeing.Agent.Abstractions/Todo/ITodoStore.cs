namespace Seeing.Agent.Abstractions.Todo;

/// <summary>
/// Todo 存储端口 - 按会话加载/保存 Todo 列表
/// </summary>
public interface ITodoStore
{
    /// <summary>
    /// 加载指定会话的 Todo 列表
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>Todo 列表</returns>
    Task<TodoList> LoadAsync(string sessionId);

    /// <summary>
    /// 保存指定会话的 Todo 列表
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="todos">Todo 列表</param>
    Task SaveAsync(string sessionId, TodoList todos);
}