using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 时序失效管理器，实现 Zep valid_at/invalid_at 模式。
/// 支持 ADD-only 策略（不删除原始记忆，标记失效）。
/// </summary>
public class MemoryTimeWindow
{
    private readonly IMemoryRepository _repository;
    private readonly ILogger<MemoryTimeWindow>? _logger;

    /// <summary>
    /// 创建 MemoryTimeWindow 实例
    /// </summary>
    /// <param name="repository">记忆存储库</param>
    /// <param name="logger">日志记录器</param>
    public MemoryTimeWindow(IMemoryRepository repository, ILogger<MemoryTimeWindow>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger;
    }

    /// <summary>
    /// 检查记忆是否在指定时间点有效。
    /// 时间窗口过滤：validAt <= now && (invalidAt == null || invalidAt > now)
    /// </summary>
    /// <param name="memory">记忆条目</param>
    /// <param name="now">当前时间点</param>
    /// <returns>如果记忆在时间窗口内有效则返回 true，否则返回 false</returns>
    public bool IsValidAt(MemoryEntry memory, DateTimeOffset now)
    {
        if (memory == null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        // 时间窗口过滤条件：
        // 1. 记忆的开始时间 <= 当前时间（记忆已生效）
        // 2. 记忆未失效（invalidAt == null）或失效时间 > 当前时间
        var isValid = memory.ValidAt <= now &&
                      (memory.InvalidAt == null || memory.InvalidAt > now);

        _logger?.LogDebug(
            "记忆有效性检查: Id={MemoryId}, ValidAt={ValidAt}, InvalidAt={InvalidAt}, Now={Now}, IsValid={IsValid}",
            memory.Id, memory.ValidAt, memory.InvalidAt, now, isValid);

        return isValid;
    }

    /// <summary>
    /// 检查记忆是否在指定时间点有效（使用 DateTime 重载）。
    /// </summary>
    /// <param name="memory">记忆条目</param>
    /// <param name="now">当前时间点</param>
    /// <returns>如果记忆在时间窗口内有效则返回 true，否则返回 false</returns>
    public bool IsValidAt(MemoryEntry memory, DateTime now)
    {
        return IsValidAt(memory, new DateTimeOffset(now));
    }

    /// <summary>
    /// 标记记忆失效时间。使用 ADD-only 策略，不删除原始记忆。
    /// 由于 MemoryEntry 是不可变的 record，此方法会创建一个新的条目并更新 InvalidAt 字段。
    /// </summary>
    /// <param name="memoryId">记忆ID</param>
    /// <param name="invalidAt">失效时间点</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新后的记忆条目</returns>
    /// <exception cref="ArgumentException">记忆ID无效</exception>
    /// <exception cref="InvalidOperationException">记忆不存在或已失效</exception>
    public async Task<MemoryEntry> SetInvalidAtAsync(
        string memoryId,
        DateTimeOffset invalidAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            throw new ArgumentException("记忆ID不能为空", nameof(memoryId));
        }

        // 获取原始记忆
        var memoryObj = await _repository.GetMemoryAsync(memoryId);
        if (memoryObj == null)
        {
            throw new InvalidOperationException($"记忆不存在: {memoryId}");
        }

        var memory = memoryObj as MemoryEntry
            ?? throw new InvalidOperationException($"记忆类型无效: {memoryId}");

        // 检查是否已经失效
        if (memory.InvalidAt != null && memory.InvalidAt <= DateTimeOffset.Now)
        {
            _logger?.LogWarning("记忆已失效，无需再次标记: {MemoryId}, InvalidAt={InvalidAt}",
                memoryId, memory.InvalidAt);
            return memory;
        }

        // 验证失效时间必须晚于生效时间
        if (invalidAt <= memory.ValidAt)
        {
            throw new ArgumentException(
                $"失效时间 ({invalidAt}) 必须晚于生效时间 ({memory.ValidAt})",
                nameof(invalidAt));
        }

        // 创建新的记忆条目（ADD-only 策略：不删除原始，创建新版本）
        // 由于 MemoryEntry 是 record，使用 with 表达式创建副本
        var updatedMemory = memory with { InvalidAt = invalidAt };

        // 保存更新后的记忆
        await _repository.SaveMemoryAsync(updatedMemory);

        _logger?.LogInformation(
            "标记记忆失效: Id={MemoryId}, ValidAt={ValidAt}, InvalidAt={InvalidAt}",
            memoryId, memory.ValidAt, invalidAt);

        return updatedMemory;
    }

    /// <summary>
    /// 标记记忆失效时间（使用 DateTime 重载）。
    /// </summary>
    public Task<MemoryEntry> SetInvalidAtAsync(
        string memoryId,
        DateTime invalidAt,
        CancellationToken cancellationToken = default)
    {
        return SetInvalidAtAsync(memoryId, new DateTimeOffset(invalidAt), cancellationToken);
    }

    /// <summary>
    /// 解决记忆冲突。使用 ADD-only 策略：
    /// 1. 将旧记忆标记为失效
    /// 2. 保留新记忆作为当前有效版本
    /// </summary>
    /// <param name="oldMemory">旧记忆条目</param>
    /// <param name="newMemory">新记忆条目</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>冲突解决结果，包含失效的旧记忆和保留的新记忆</returns>
    /// <exception cref="ArgumentNullException">参数为空</exception>
    public async Task<ConflictResolutionResult> ResolveConflictAsync(
        MemoryEntry oldMemory,
        MemoryEntry newMemory,
        CancellationToken cancellationToken = default)
    {
        if (oldMemory == null)
        {
            throw new ArgumentNullException(nameof(oldMemory));
        }
        if (newMemory == null)
        {
            throw new ArgumentNullException(nameof(newMemory));
        }

        _logger?.LogInformation(
            "解决记忆冲突: OldId={OldId}, NewId={NewId}, Type={Type}",
            oldMemory.Id, newMemory.Id, oldMemory.Type);

        // 验证新记忆的生效时间必须晚于旧记忆
        if (newMemory.ValidAt < oldMemory.ValidAt)
        {
            _logger?.LogWarning(
                "新记忆生效时间早于旧记忆，可能存在时序问题: OldValidAt={OldValidAt}, NewValidAt={NewValidAt}",
                oldMemory.ValidAt, newMemory.ValidAt);
        }

        // 如果旧记忆尚未失效，将其标记为失效
        // 使用新记忆的生效时间作为旧记忆的失效时间
        MemoryEntry? invalidatedOldMemory = null;

        if (oldMemory.InvalidAt == null || oldMemory.InvalidAt > DateTimeOffset.Now)
        {
            // 设置失效时间为新记忆的生效时间
            var invalidAt = newMemory.ValidAt;

            // 如果旧记忆的生效时间等于新记忆的生效时间（同时创建），
            // 将失效时间设置为当前时间
            if (invalidAt <= oldMemory.ValidAt)
            {
                invalidAt = DateTimeOffset.Now;
            }

            invalidatedOldMemory = await SetInvalidAtAsync(oldMemory.Id, invalidAt, cancellationToken);

            _logger?.LogDebug(
                "旧记忆已标记失效: Id={OldId}, InvalidAt={InvalidAt}",
                oldMemory.Id, invalidAt);
        }

        // 保存新记忆（如果尚未保存）
        await _repository.SaveMemoryAsync(newMemory);

        _logger?.LogInformation(
            "冲突解决完成: 失效记忆={OldId}, 有效记忆={NewId}",
            oldMemory.Id, newMemory.Id);

        return new ConflictResolutionResult
        {
            InvalidatedMemory = invalidatedOldMemory,
            ActiveMemory = newMemory,
            ResolvedAt = DateTimeOffset.Now
        };
    }

    /// <summary>
    /// 批量标记记忆失效
    /// </summary>
    /// <param name="memoryIds">记忆ID列表</param>
    /// <param name="invalidAt">失效时间点</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>成功失效的记忆数量</returns>
    public async Task<int> BatchSetInvalidAtAsync(
        string[] memoryIds,
        DateTimeOffset invalidAt,
        CancellationToken cancellationToken = default)
    {
        if (memoryIds == null || memoryIds.Length == 0)
        {
            return 0;
        }

        var successCount = 0;

        foreach (var memoryId in memoryIds)
        {
            try
            {
                await SetInvalidAtAsync(memoryId, invalidAt, cancellationToken);
                successCount++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "批量标记失效失败: {MemoryId}", memoryId);
            }
        }

        _logger?.LogInformation(
            "批量标记失效完成: 总数={Total}, 成功={Success}",
            memoryIds.Length, successCount);

        return successCount;
    }

    /// <summary>
    /// 立即使记忆失效（设置 InvalidAt 为当前时间）
    /// </summary>
    /// <param name="memoryId">记忆ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新后的记忆条目</returns>
    public Task<MemoryEntry> InvalidateNowAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        return SetInvalidAtAsync(memoryId, DateTimeOffset.Now, cancellationToken);
    }

    /// <summary>
    /// 检查记忆是否已失效
    /// </summary>
    /// <param name="memory">记忆条目</param>
    /// <returns>如果记忆已失效返回 true，否则返回 false</returns>
    public bool IsInvalidated(MemoryEntry memory)
    {
        if (memory == null)
        {
            return true;
        }

        return memory.InvalidAt != null && memory.InvalidAt <= DateTimeOffset.Now;
    }

    /// <summary>
    /// 获取记忆的有效时间窗口
    /// </summary>
    /// <param name="memory">记忆条目</param>
    /// <returns>时间窗口元组（开始时间，结束时间，是否有效）</returns>
    public (DateTimeOffset ValidFrom, DateTimeOffset? ValidUntil, bool IsValid) GetValidityWindow(MemoryEntry memory)
    {
        if (memory == null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        var now = DateTimeOffset.Now;
        var isValid = IsValidAt(memory, now);

        return (memory.ValidAt, memory.InvalidAt, isValid);
    }
}

/// <summary>
/// 冲突解决结果
/// </summary>
public record ConflictResolutionResult
{
    /// <summary>
    /// 被标记失效的旧记忆（如果原未失效）
    /// </summary>
    public MemoryEntry? InvalidatedMemory { get; init; }

    /// <summary>
    /// 当前有效的记忆
    /// </summary>
    public MemoryEntry ActiveMemory { get; init; } = null!;

    /// <summary>
    /// 冲突解决时间
    /// </summary>
    public DateTimeOffset ResolvedAt { get; init; }
}