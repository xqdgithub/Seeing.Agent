using System.Text.Json.Serialization;

namespace Seeing.Agent.Core.Todo;

/// <summary>
/// Todo 项状态
/// </summary>
public enum TodoStatus
{
    /// <summary>待处理</summary>
    Pending,
    /// <summary>进行中</summary>
    InProgress,
    /// <summary>已完成</summary>
    Completed,
    /// <summary>已取消</summary>
    Cancelled
}

/// <summary>
/// Todo 项
/// </summary>
public class TodoItem
{
    /// <summary>任务唯一标识</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>任务内容描述</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>任务状态</summary>
    [JsonPropertyName("status")]
    public TodoStatus Status { get; set; } = TodoStatus.Pending;

    /// <summary>任务优先级（low/medium/high）</summary>
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "medium";

    /// <summary>创建时间</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>完成时间（可选）</summary>
    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Todo 列表 - 按会话分组
/// </summary>
public class TodoList
{
    /// <summary>会话 ID</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Todo 项列表</summary>
    [JsonPropertyName("items")]
    public List<TodoItem> Items { get; set; } = new();

    /// <summary>创建时间</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>最后更新时间</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}