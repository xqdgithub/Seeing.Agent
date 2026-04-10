namespace Seeing.Agent.Core.Todo;

/// <summary>
/// Todo 管理器接口 - 提供跨会话的 Todo 持久化管理
/// </summary>
public interface ITodoManager
{
    /// <summary>
    /// 加载指定会话的 Todo 列表
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>Todo 列表，如果不存在则返回空列表</returns>
    Task<TodoList> LoadAsync(string sessionId);

    /// <summary>
    /// 保存 Todo 列表
    /// </summary>
    /// <param name="todoList">要保存的 Todo 列表</param>
    Task SaveAsync(TodoList todoList);

    /// <summary>
    /// 添加新的 Todo 项
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="content">任务内容</param>
    /// <param name="priority">优先级（low/medium/high）</param>
    /// <returns>新创建的 Todo 项</returns>
    Task<TodoItem> AddAsync(string sessionId, string content, string priority = "medium");

    /// <summary>
    /// 更新 Todo 项状态
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="todoId">Todo 项 ID</param>
    /// <param name="status">新状态</param>
    /// <returns>更新后的 Todo 项，如果不存在则返回 null</returns>
    Task<TodoItem?> UpdateStatusAsync(string sessionId, string todoId, TodoStatus status);

    /// <summary>
    /// 删除指定会话的所有 Todo 数据
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    Task DeleteAsync(string sessionId);
}