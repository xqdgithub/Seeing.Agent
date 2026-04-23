using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Core
{
    /// <summary>
    /// 遗忘管理器，实现主动遗忘和被动衰减机制
    /// </summary>
    public class MemoryForgetManager : IMemoryForgetter
    {
        private readonly IMemoryRepository _repository;
        private readonly IMemoryScorer _scorer;
        private readonly MemoryScoreOptions _scoreOptions;
        private readonly MemoryStoreOptions _storeOptions;
        private readonly ILogger<MemoryForgetManager>? _logger;

        /// <summary>
        /// 衰减周期（天数），超过此周期的记忆开始衰减
        /// </summary>
        private const double DecayStartDays = 7.0;

        /// <summary>
        /// 最大衰减系数，每次衰减最多降低 importance 的此比例
        /// </summary>
        private const double MaxDecayFactor = 0.1;

        /// <summary>
        /// 创建 MemoryForgetManager 实例
        /// </summary>
        public MemoryForgetManager(
            IMemoryRepository repository,
            IMemoryScorer scorer,
            IOptions<MemoryOptions> options,
            ILogger<MemoryForgetManager>? logger = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
            _scoreOptions = options.Value?.MemoryScore ?? new MemoryScoreOptions();
            _storeOptions = options.Value?.MemoryStore ?? new MemoryStoreOptions();
            _logger = logger;
        }

        /// <summary>
        /// 归档低于阈值的记忆（主动遗忘）
        /// ADD-only 策略：不删除原始记忆，移到 archive 目录
        /// </summary>
        public async Task<int> ArchiveLowScoreMemoriesAsync(double threshold, CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation("开始主动遗忘，阈值: {Threshold}", threshold);

            var candidates = await GetForgettingCandidatesAsync(threshold, cancellationToken);
            if (candidates.Count == 0)
            {
                _logger?.LogInformation("没有需要归档的记忆");
                return 0;
            }

            var archivedCount = 0;
            foreach (var memory in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 创建归档记忆（更改类型为 Archive）
                    // 使用记录的 with 表达式避免显式构造器参数问题
                    var archivedMemory = memory with {
                        Type = MemoryType.Archive,
                        Content = memory.Content,
                        Metadata = memory.Metadata,
                        CreatedAt = memory.CreatedAt,
                        ValidAt = memory.ValidAt,
                        InvalidAt = memory.InvalidAt ?? DateTimeOffset.UtcNow
                    };

                    // 保存归档记忆
                    await _repository.SaveMemoryAsync(archivedMemory);
                    archivedCount++;

                    _logger?.LogDebug("记忆已归档: {MemoryId}, 原类型: {OriginalType}", 
                        memory.Id, memory.Type);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "归档记忆失败: {MemoryId}", memory.Id);
                }
            }

            _logger?.LogInformation("主动遗忘完成，归档 {Count} 条记忆", archivedCount);
            return archivedCount;
        }

        /// <summary>
        /// 应用被动衰减，降低长期未访问记忆的重要性
        /// 基于年龄计算衰减因子，逐步降低 importance 值
        /// </summary>
        public async Task<int> ApplyDecayAsync(CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation("开始被动衰减");

            var memories = await _repository.ListMemoriesAsync();
            var memoryEntries = memories
                .OfType<MemoryEntry>()
                .Where(m => m.Type != MemoryType.Archive) // 不衰减已归档记忆
                .ToList();

            var decayedCount = 0;
            var now = DateTimeOffset.UtcNow;

            foreach (var memory in memoryEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 计算年龄（天数）
                var ageDays = (now - memory.ValidAt).TotalDays;

                // 只有超过 DecayStartDays 的记忆才衰减
                if (ageDays < DecayStartDays)
                {
                    continue;
                }

                // 计算衰减因子（基于年龄指数衰减）
                var decayFactor = CalculateDecayFactor(ageDays);

                // 计算新的 importance 值
                var currentImportance = memory.Metadata.Importance;
                var newImportance = Math.Max(0.0, currentImportance * (1.0 - decayFactor));

                // 只有 importance 变化超过阈值才更新
                if (Math.Abs(currentImportance - newImportance) < 0.01)
                {
                    continue;
                }

                try
                {
                    // 创建衰减后的记忆
                    var decayedMetadata = new MemoryMetadata(
                        memory.Metadata.SessionId,
                        memory.Metadata.AgentId,
                        memory.Metadata.Source,
                        memory.Metadata.Tags,
                        memory.Metadata.Confidence,
                        newImportance // 降低后的 importance
                    );

                    var decayedMemory = memory with {
                        Metadata = decayedMetadata,
                        InvalidAt = memory.InvalidAt
                    };

                    // 保存更新后的记忆
                    await _repository.SaveMemoryAsync(decayedMemory);
                    decayedCount++;

                    _logger?.LogDebug("记忆衰减: {MemoryId}, age={AgeDays}天, importance {Old}->{New}", 
                        memory.Id, ageDays, currentImportance, newImportance);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "衰减记忆失败: {MemoryId}", memory.Id);
                }
            }

            _logger?.LogInformation("被动衰减完成，衰减 {Count} 条记忆", decayedCount);
            return decayedCount;
        }

        /// <summary>
        /// 获取候选遗忘记忆列表
        /// 返回低于阈值的记忆，不执行实际操作
        /// </summary>
        public async Task<IReadOnlyList<MemoryEntry>> GetForgettingCandidatesAsync(
            double? threshold = null, 
            CancellationToken cancellationToken = default)
        {
            var effectiveThreshold = threshold ?? _scoreOptions.ForgettingThreshold;

            _logger?.LogDebug("获取遗忘候选，阈值: {Threshold}", effectiveThreshold);

            var memories = await _repository.ListMemoriesAsync();
            var candidates = new List<MemoryEntry>();

            foreach (var memoryObj in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (memoryObj is not MemoryEntry memory)
                {
                    continue;
                }

                // 已归档的记忆不再作为候选
                if (memory.Type == MemoryType.Archive)
                {
                    continue;
                }

                // 使用评分器计算分数
                var scoreOptions = new Dictionary<string, object>
                {
                    ["importanceWeight"] = _scoreOptions.ImportanceWeight,
                    ["accessFreqWeight"] = _scoreOptions.AccessFreqWeight,
                    ["ageWeight"] = _scoreOptions.AgeWeight
                };

                var score = await _scorer.ScoreAsync(memory, scoreOptions);

                // 低于阈值的记忆加入候选列表
                if (score < effectiveThreshold)
                {
                    candidates.Add(memory);
                    _logger?.LogDebug("候选记忆: {MemoryId}, score={Score}, threshold={Threshold}", 
                        memory.Id, score, effectiveThreshold);
                }
            }

            _logger?.LogInformation("找到 {Count} 条遗忘候选", candidates.Count);
            return candidates.AsReadOnly();
        }

        /// <summary>
        /// 计算衰减因子（指数衰减模型）
        /// </summary>
        /// <param name="ageDays">记忆年龄（天数）</param>
        /// <returns>衰减因子（0-1之间）</returns>
        private static double CalculateDecayFactor(double ageDays)
        {
            // 指数衰减：decayFactor = MaxDecayFactor * (1 - e^(-ageDays / halfLife))
            // halfLife = 30 天，表示 30 天后衰减达到 MaxDecayFactor 的 50%
            const double halfLife = 30.0;
            
            var factor = MaxDecayFactor * (1.0 - Math.Exp(-ageDays / halfLife));
            
            // 确保衰减因子不超过 MaxDecayFactor
            return Math.Min(factor, MaxDecayFactor);
        }
    }
}
