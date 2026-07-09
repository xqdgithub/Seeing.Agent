using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using System.Collections.Concurrent;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 批量合并结果。
/// </summary>
public class BatchConsolidationResult
{
    /// <summary>
    /// 是否成功执行合并。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 处理的会话数量。
    /// </summary>
    public int SessionsProcessed { get; set; }

    /// <summary>
    /// 发现的重复组数量。
    /// </summary>
    public int DuplicateGroupsFound { get; set; }

    /// <summary>
    /// 创建的合并摘要数量。
    /// </summary>
    public int SummaryCreated { get; set; }

    /// <summary>
    /// 执行过程中的消息或错误描述。
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// Memory Hybrid 写入调度器。
/// 实现双路径策略：
/// - Hot Path: 关键记忆立即写入
/// - Background: 批量 Consolidation（去重合并）
/// </summary>
public class MemoryOrchestrator
{
    private readonly IMemoryManager _memoryManager;
    private readonly IMemoryRepository _repository;
    private readonly MemoryDeduplicator _deduplicator;
    private readonly MemoryOrchestratorOptions _options;
    private readonly ILogger<MemoryOrchestrator>? _logger;

    // 待合并队列：sessionId -> 队列加入时间
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consolidationQueue = new();

    // 队列锁，防止并发合并
    private readonly SemaphoreSlim _consolidationLock = new(1, 1);

    // 定时器，用于触发周期性合并
    private Timer? _consolidationTimer;

    /// <summary>
    /// 创建 MemoryOrchestrator 实例。
    /// </summary>
    /// <param name="memoryManager">Memory 管理器</param>
    /// <param name="repository">Memory 存储仓库</param>
    /// <param name="deduplicator">去重合并器</param>
    /// <param name="options">配置选项</param>
    /// <param name="logger">日志记录器</param>
    public MemoryOrchestrator(
        IMemoryManager memoryManager,
        IMemoryRepository repository,
        MemoryDeduplicator deduplicator,
        MemoryOrchestratorOptions? options = null,
        ILogger<MemoryOrchestrator>? logger = null)
    {
        _memoryManager = memoryManager ?? throw new ArgumentNullException(nameof(memoryManager));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _deduplicator = deduplicator ?? throw new ArgumentNullException(nameof(deduplicator));
        _options = options ?? new MemoryOrchestratorOptions();
        _logger = logger;

        // 启动定时合并任务
        StartConsolidationTimer();
    }

    /// <summary>
    /// 立即捕获并写入关键记忆（Hot Path）。
    /// 根据配置的重要性阈值决定是否立即写入。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="type">记忆类型</param>
    /// <param name="content">记忆内容</param>
    /// <param name="importance">重要性分数，默认 1.0（关键记忆）</param>
    /// <param name="confidence">置信度分数，默认 1.0</param>
    /// <param name="agentId">Agent ID，可选</param>
    /// <param name="source">来源，可选</param>
    /// <param name="tags">标签列表，可选</param>
    /// <returns>创建的记忆 ID</returns>
    public async Task<string> CaptureImmediateAsync(
        string sessionId,
        MemoryType type,
        string content,
        double importance = 1.0,
        double confidence = 1.0,
        string? agentId = null,
        string? source = null,
        IReadOnlyList<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("content 不能为空", nameof(content));
        }

        // 检查是否达到立即写入阈值
        if (importance < _options.ImmediateWriteThreshold)
        {
            _logger?.LogWarning(
                "记忆重要性 {Importance} 未达到立即写入阈值 {Threshold}, 建议使用批量合并队列",
                importance, _options.ImmediateWriteThreshold);
        }

        _logger?.LogInformation(
            "立即写入关键记忆: Session={SessionId}, Type={Type}, Importance={Importance}",
            sessionId, type, importance);

        var memoryId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;

        var metadata = new MemoryMetadata(
            SessionId: sessionId,
            AgentId: agentId ?? string.Empty,
            Source: source ?? "immediate_capture",
            Tags: tags ?? new List<string> { "immediate" },
            Confidence: confidence,
            Importance: importance
        );

        var entry = new MemoryEntry(
            Id: memoryId,
            Type: type,
            Content: content,
            Metadata: metadata,
            CreatedAt: now,
            ValidAt: now,
            InvalidAt: null
        );

        // 使用 MemoryManager 立即写入
        await _memoryManager.CreateMemoryAsync(entry);

        _logger?.LogDebug("关键记忆已写入: {MemoryId}", memoryId);

        return memoryId;
    }

    /// <summary>
    /// 将会话加入批量合并队列（Background Path）。
    /// 队列达到容量阈值时自动触发合并。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>是否成功加入队列</returns>
    public async Task<bool> QueueForConsolidationAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }

        // 添加到队列（如果已存在则更新时间）
        _consolidationQueue.AddOrUpdate(
            sessionId,
            DateTimeOffset.Now,
            (_, _) => DateTimeOffset.Now);

        var queueSize = _consolidationQueue.Count;

        _logger?.LogInformation(
            "会话 {SessionId} 已加入合并队列，当前队列大小: {QueueSize}",
            sessionId, queueSize);

        // 检查是否达到容量阈值，触发强制合并
        if (queueSize >= _options.ConsolidationQueueCapacity)
        {
            _logger?.LogWarning(
                "合并队列达到容量阈值 {Capacity}, 触发强制合并",
                _options.ConsolidationQueueCapacity);

            // 异步触发合并，不阻塞当前操作
            _ = Task.Run(async () =>
            {
                await RunConsolidationAsync();
            });
        }

        return true;
    }

    /// <summary>
    /// 执行批量合并（Background Path）。
    /// 对队列中的所有会话执行去重检测和合并。
    /// 无 LLM 参与，使用简单的文本相似度检测。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合并结果</returns>
    public async Task<BatchConsolidationResult> RunConsolidationAsync(CancellationToken cancellationToken = default)
    {
        // 获取锁，防止并发合并
        if (!await _consolidationLock.WaitAsync(0, cancellationToken))
        {
            _logger?.LogDebug("合并任务已在执行中，跳过本次请求");
            return new BatchConsolidationResult
            {
                Success = false,
                Message = "合并任务已在执行中"
            };
        }

        try
        {
            _logger?.LogInformation("开始执行批量合并任务");

            // 获取待合并的会话列表
            var sessionIds = _consolidationQueue.Keys.ToList();

            if (sessionIds.Count == 0)
            {
                _logger?.LogDebug("合并队列为空，无需执行");
                return new BatchConsolidationResult
                {
                    Success = true,
                    SessionsProcessed = 0,
                    Message = "合并队列为空"
                };
            }

            var result = new BatchConsolidationResult
            {
                Success = true,
                SessionsProcessed = sessionIds.Count
            };

            var totalDuplicateGroups = 0;
            var totalSummaryCreated = 0;

            // 对每个会话执行去重检测和合并
            foreach (var sessionId in sessionIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogWarning("合并任务被取消");
                    result.Message = "合并任务被取消";
                    break;
                }

                try
                {
                    var (duplicateGroups, summariesCreated) = await ProcessSessionConsolidationAsync(
                        sessionId, cancellationToken);

                    totalDuplicateGroups += duplicateGroups;
                    totalSummaryCreated += summariesCreated;

                    // 处理完成后移除队列
                    _consolidationQueue.TryRemove(sessionId, out _);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "会话 {SessionId} 合并失败", sessionId);
                    // 继续处理其他会话
                }
            }

            result.DuplicateGroupsFound = totalDuplicateGroups;
            result.SummaryCreated = totalSummaryCreated;
            result.Message = $"批量合并完成: 处理 {result.SessionsProcessed} 个会话, 发现 {totalDuplicateGroups} 个重复组";

            _logger?.LogInformation(
                "批量合并完成: Sessions={SessionsProcessed}, DuplicateGroups={DuplicateGroups}, Summaries={SummaryCreated}",
                result.SessionsProcessed, totalDuplicateGroups, totalSummaryCreated);

            return result;
        }
        finally
        {
            _consolidationLock.Release();
        }
    }

    /// <summary>
    /// 处理单个会话的合并。
    /// </summary>
    private async Task<(int DuplicateGroups, int SummariesCreated)> ProcessSessionConsolidationAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        _logger?.LogDebug("处理会话 {SessionId} 的合并", sessionId);

        // 检查会话记忆数量是否达到最小阈值
        var allMemories = await _repository.ListMemoriesAsync();
        var sessionMemories = allMemories
            .Cast<MemoryEntry>()
            .Where(m => m.Metadata?.SessionId == sessionId)
            .ToList();

        if (sessionMemories.Count < _options.MinMemoriesForConsolidation)
        {
            _logger?.LogDebug(
                "会话 {SessionId} 记忆数量 {Count} 未达到最小阈值 {Min}, 跳过合并",
                sessionId, sessionMemories.Count, _options.MinMemoriesForConsolidation);
            return (0, 0);
        }

        // 使用 MemoryDeduplicator 查找重复组
        var duplicateGroups = await _deduplicator.FindDuplicatesAsync(
            sessionId,
            _options.DeduplicationThreshold);

        if (duplicateGroups.Count == 0)
        {
            _logger?.LogDebug("会话 {SessionId} 未发现重复记忆", sessionId);
            return (0, 0);
        }

        var summariesCreated = 0;

        // 对每个重复组执行合并
        foreach (var group in duplicateGroups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var mergeResult = await _deduplicator.MergeDuplicatesAsync(
                group.Entries,
                _options.CreateSummaryOnConsolidation);

            if (mergeResult.Success && mergeResult.MergedMemoryId != null)
            {
                summariesCreated++;
                _logger?.LogDebug(
                    "重复组合并完成: 创建摘要 {MergedId}, 保留 {Retained} 条原始记忆",
                    mergeResult.MergedMemoryId, mergeResult.RetainedCount);
            }
        }

        return (duplicateGroups.Count, summariesCreated);
    }

    /// <summary>
    /// 启动定时合并任务。
    /// </summary>
    private void StartConsolidationTimer()
    {
        if (_options.ConsolidationIntervalSeconds <= 0)
        {
            _logger?.LogDebug("定时合并已禁用（间隔 <= 0）");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.ConsolidationIntervalSeconds);

        _consolidationTimer = new Timer(
            async _ =>
            {
                try
                {
                    if (_consolidationQueue.Count > 0)
                    {
                        _logger?.LogDebug("定时合并触发，队列大小: {QueueSize}", _consolidationQueue.Count);
                        await RunConsolidationAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "定时合并任务执行失败");
                }
            },
            null,
            interval,
            interval);

        _logger?.LogInformation(
            "定时合并任务已启动，间隔: {Interval}秒",
            _options.ConsolidationIntervalSeconds);
    }

    /// <summary>
    /// 获取当前合并队列状态。
    /// </summary>
    /// <returns>队列中的会话 ID 及加入时间</returns>
    public IReadOnlyDictionary<string, DateTimeOffset> GetQueueStatus()
    {
        return new Dictionary<string, DateTimeOffset>(_consolidationQueue);
    }

    /// <summary>
    /// 停止定时合并任务并清理资源。
    /// </summary>
    public void Dispose()
    {
        _consolidationTimer?.Dispose();
        _consolidationLock.Dispose();
        _logger?.LogInformation("MemoryOrchestrator 已停止");
    }
}