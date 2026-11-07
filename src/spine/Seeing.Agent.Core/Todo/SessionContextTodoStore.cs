using Seeing.Agent.Abstractions.Todo;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Todo;

/// <summary>
/// 基于 Session Context 的 Todo 存储实现（默认适配器）。
/// <para>
/// 将 Todo 列表桥接进会话内存 Context，魔法键 "todos" 封装在适配器内部，
/// 不再泄漏到领域层（端口-适配器模式）。
/// </para>
/// <para>
/// <b>为什么进出都做拷贝（不要改回直接存取 <c>todos.Items</c>）：</b>
/// <see cref="SessionData.Clone"/> 对 <c>Context</c> 只做浅拷贝（新字典容器，值仍共享引用），
/// 而落盘线程会序列化那个克隆。若这里把<b>活列表</b>放进 Context，另一线程（工具执行）随后
/// <c>Add</c> 同一列表，序列化就会枚举到正在修改的集合——表现为会话文件半截/抛
/// <c>InvalidOperationException</c>。因此：
/// <list type="bullet">
/// <item><see cref="SaveAsync"/> 存入一份独立副本（快照语义由适配器保证，不依赖调用方自觉）。</item>
/// <item><see cref="LoadAsync"/> 返回副本，调用方修改不会反向影响 Context 里的快照。</item>
/// </list>
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
        var stored = session?.GetContext<List<TodoItem>>(TodoContextKey);

        // 返回副本：调用方（工具/UI）对返回值的修改不得穿透到会话 Context，
        // 否则「读到的」与「落盘的」会是同一个可变对象。
        var items = stored is null ? new List<TodoItem>() : stored.Select(Clone).ToList();
        return Task.FromResult(new TodoList { SessionId = sessionId, Items = items });
    }

    /// <inheritdoc/>
    public Task SaveAsync(string sessionId, TodoList todos)
    {
        var session = _sessionManager.Get(sessionId);
        if (session == null)
            throw new InvalidOperationException($"会话不存在: {sessionId}");

        // 存入快照副本：SessionData.Clone 是浅拷贝，Context 中的值对象会被落盘线程共享。
        // 若直接存 todos.Items（活列表），执行线程后续 Add 会与序列化并发。
        session.SetContext(TodoContextKey, todos.Items.Select(Clone).ToList());
        return Task.CompletedTask;
    }

    /// <summary>浅拷贝单项（<see cref="TodoItem"/> 全为值类型/字符串字段，无需深拷贝引用类型成员）。</summary>
    private static TodoItem Clone(TodoItem item) => new()
    {
        Id = item.Id,
        Content = item.Content,
        Status = item.Status,
        Priority = item.Priority,
        CreatedAt = item.CreatedAt,
        CompletedAt = item.CompletedAt
    };
}
