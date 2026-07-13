using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Core.Todo;

/// <summary>
/// Todo 管理器 - 线程安全的 Todo 持久化实现
/// </summary>
public class TodoManager : ITodoManager
{
    private readonly ILogger<TodoManager> _logger;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    /// <summary>
    /// 创建 TodoManager 实例
    /// </summary>
    /// <param name="logger">日志器</param>
    /// <param name="workspaceProvider">工作区提供器</param>
    public TodoManager(
        ILogger<TodoManager> logger,
        IWorkspaceProvider workspaceProvider)
    {
        _logger = logger;
        _workspaceProvider = workspaceProvider;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <inheritdoc/>
    public async Task<TodoList> LoadAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("sessionId 为空，返回空 Todo 列表");
            return new TodoList { SessionId = sessionId };
        }

        var filePath = GetFilePath(sessionId);
        var fileLock = GetFileLock(sessionId);

        await fileLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("Todo 文件不存在，返回空列表: {Path}", filePath);
                return new TodoList { SessionId = sessionId };
            }

            var json = await File.ReadAllTextAsync(filePath);
            var todoList = JsonSerializer.Deserialize<TodoList>(json, _jsonOptions);

            if (todoList == null)
            {
                _logger.LogWarning("Todo 文件反序列化为 null，返回空列表: {Path}", filePath);
                return new TodoList { SessionId = sessionId };
            }

            _logger.LogDebug("已加载 Todo 列表: SessionId={SessionId}, Items={Count}",
                sessionId, todoList.Items.Count);

            return todoList;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Todo 文件格式错误，返回空列表: {Path}", filePath);
            return new TodoList { SessionId = sessionId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 Todo 文件失败: {Path}", filePath);
            throw;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(TodoList todoList)
    {
        if (string.IsNullOrEmpty(todoList.SessionId))
        {
            _logger.LogWarning("sessionId 为空，跳过保存");
            return;
        }

        var filePath = GetFilePath(todoList.SessionId);
        var fileLock = GetFileLock(todoList.SessionId);

        await fileLock.WaitAsync();
        try
        {
            var storagePath = GetStoragePath();
            // 确保目录存在
            if (!Directory.Exists(storagePath))
            {
                Directory.CreateDirectory(storagePath);
                _logger.LogDebug("已创建 Todo 存储目录: {Directory}", storagePath);
            }

            todoList.UpdatedAt = DateTimeOffset.Now;
            var json = JsonSerializer.Serialize(todoList, _jsonOptions);

            await File.WriteAllTextAsync(filePath, json);

            _logger.LogInformation("已保存 Todo 列表: SessionId={SessionId}, Items={Count}, Path={Path}",
                todoList.SessionId, todoList.Items.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Todo 文件失败: {Path}", filePath);
            throw;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<TodoItem> AddAsync(string sessionId, string content, string priority = "medium")
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }

        if (string.IsNullOrEmpty(content))
        {
            throw new ArgumentException("content 不能为空", nameof(content));
        }

        // 验证优先级
        priority = NormalizePriority(priority);

        var todoList = await LoadAsync(sessionId);

        var todoItem = new TodoItem
        {
            Id = GenerateTodoId(),
            Content = content,
            Status = TodoStatus.Pending,
            Priority = priority,
            CreatedAt = DateTimeOffset.Now,
            CompletedAt = null
        };

        todoList.Items.Add(todoItem);
        await SaveAsync(todoList);

        _logger.LogInformation("已添加 Todo 项: Id={Id}, Content={Content}, Priority={Priority}",
            todoItem.Id, todoItem.Content, todoItem.Priority);

        return todoItem;
    }

    /// <inheritdoc/>
    public async Task<TodoItem?> UpdateStatusAsync(string sessionId, string todoId, TodoStatus status)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }

        if (string.IsNullOrEmpty(todoId))
        {
            throw new ArgumentException("todoId 不能为空", nameof(todoId));
        }

        var todoList = await LoadAsync(sessionId);
        var todoItem = todoList.Items.FirstOrDefault(t => t.Id == todoId);

        if (todoItem == null)
        {
            _logger.LogWarning("未找到 Todo 项: Id={Id}, SessionId={SessionId}", todoId, sessionId);
            return null;
        }

        var oldStatus = todoItem.Status;
        todoItem.Status = status;

        // 如果状态变为已完成或已取消，设置完成时间
        if (status == TodoStatus.Completed || status == TodoStatus.Cancelled)
        {
            todoItem.CompletedAt = DateTimeOffset.Now;
        }
        // 如果从已完成/已取消变为其他状态，清除完成时间
        else if (oldStatus == TodoStatus.Completed || oldStatus == TodoStatus.Cancelled)
        {
            todoItem.CompletedAt = null;
        }

        await SaveAsync(todoList);

        _logger.LogInformation("已更新 Todo 状态: Id={Id}, OldStatus={OldStatus}, NewStatus={NewStatus}",
            todoId, oldStatus, status);

        return todoItem;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("sessionId 为空，跳过删除");
            return;
        }

        var filePath = GetFilePath(sessionId);
        var fileLock = GetFileLock(sessionId);

        await fileLock.WaitAsync();
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("已删除 Todo 文件: {Path}", filePath);
            }

            // 清理锁
            _fileLocks.TryRemove(sessionId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除 Todo 文件失败: {Path}", filePath);
            throw;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 获取存储路径
    /// </summary>
    private string GetStoragePath()
    {
        return Path.Combine(_workspaceProvider.WorkspaceRoot, ".seeing", "todos");
    }

    /// <summary>
    /// 获取文件路径
    /// </summary>
    private string GetFilePath(string sessionId)
    {
        // 使用安全的文件名（替换可能不安全的字符）
        var safeSessionId = sessionId.Replace("/", "_").Replace("\\", "_").Replace("..", "_");
        return Path.Combine(GetStoragePath(), $"{safeSessionId}.json");
    }

    /// <summary>
    /// 获取文件锁（确保同一会话的并发操作安全）
    /// </summary>
    private SemaphoreSlim GetFileLock(string sessionId)
    {
        return _fileLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// 生成 Todo ID
    /// </summary>
    private static string GenerateTodoId()
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMddHHmmss");
        var random = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"todo_{timestamp}_{random}";
    }

    /// <summary>
    /// 标准化优先级
    /// </summary>
    private static string NormalizePriority(string priority)
    {
        return priority.ToLowerInvariant() switch
        {
            "low" => "low",
            "high" => "high",
            _ => "medium"
        };
    }
}