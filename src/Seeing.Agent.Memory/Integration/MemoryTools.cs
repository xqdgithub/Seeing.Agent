using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Tools.Attributes;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// 记忆管理工具集，使用 [Tool] 注解定义。
/// LLM 可通过这些工具操作记忆系统。
/// </summary>
public class MemoryTools
{
    private readonly IMemoryManager _manager;
    private readonly ILogger<MemoryTools>? _logger;

    /// <summary>
    /// 创建 MemoryTools 实例
    /// </summary>
    public MemoryTools(IMemoryManager manager, ILogger<MemoryTools>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
    }

    /// <summary>
    /// 存储记忆到会话
    /// </summary>
    [Tool("存储记忆到当前会话")]
    public async Task<string> StoreMemoryAsync(
        [ToolParam("记忆内容")] string content,
        [ToolParam("记忆类型: semantic/episodic/procedural")] string type,
        [ToolParam("会话 ID")] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "错误：记忆内容不能为空";
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "错误：会话 ID 不能为空";
        }

        _logger?.LogInformation("存储记忆: Type={Type}, Session={SessionId}", type, sessionId);

        // 解析记忆类型
        MemoryType memoryType;
        if (!Enum.TryParse<MemoryType>(type, ignoreCase: true, out memoryType))
        {
            memoryType = MemoryType.Semantic; // 默认语义记忆
        }

        var entry = CreateEntry(content, memoryType, sessionId);
        await _manager.CreateMemoryAsync(entry);

        return $"记忆已保存 (ID: {entry.Id}, 类型: {memoryType})";
    }

    /// <summary>
    /// 搜索记忆
    /// </summary>
    [Tool("搜索记忆", Name = "search_memory")]
    public async Task<string> SearchMemoryAsync(
        [ToolParam("搜索关键词")] string query,
        [ToolParam("会话 ID（可选）")] string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "错误：搜索关键词不能为空";
        }

        _logger?.LogInformation("搜索记忆: Query={Query}, Session={SessionId}", query, sessionId);

        var filter = new MemoryFilter
        {
            SessionId = sessionId ?? string.Empty
        };

        var result = await _manager.SearchMemoriesAsync(query, filter);

        if (result.TotalCount == 0)
        {
            return "未找到匹配的记忆";
        }

        return $"找到 {result.TotalCount} 条记忆";
    }

    /// <summary>
    /// 检索记忆
    /// </summary>
    [Tool("检索记忆", Name = "recall_memory")]
    public async Task<string> RecallMemoryAsync(
        [ToolParam("记忆 ID")] string memoryId)
    {
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            return "错误：记忆 ID 不能为空";
        }

        _logger?.LogInformation("检索记忆: ID={MemoryId}", memoryId);

        var memory = await _manager.GetMemoryAsync(memoryId);

        if (memory == null)
        {
            return $"未找到记忆: {memoryId}";
        }

        return $"记忆内容:\n{memory.Content}";
    }

    /// <summary>
    /// 创建 MemoryEntry 辅助方法
    /// </summary>
    private MemoryEntry CreateEntry(string content, MemoryType type, string sessionId)
    {
        var memoryId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;

        var metadata = new MemoryMetadata(
            SessionId: sessionId,
            AgentId: "memory.tool",
            Source: "tool.store_memory",
            Tags: new[] { "tool", type.ToString().ToLower() },
            Confidence: 1.0,
            Importance: 0.8
        );

        return new MemoryEntry(
            Id: memoryId,
            Type: type,
            Content: content,
            Metadata: metadata,
            CreatedAt: now,
            ValidAt: now,
            InvalidAt: null
        );
    }
}