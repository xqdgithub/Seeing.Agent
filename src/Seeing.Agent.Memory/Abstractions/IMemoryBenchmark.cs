using System.Collections.Generic;
using System.Threading.Tasks;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// Task 32: MemoryBenchmark 评测接口预留
/// 定义 Memory 系统性能评测的接口
/// </summary>
public interface IMemoryBenchmark
{
    /// <summary>
    /// 执行 Memory 系统性能基准测试
    /// </summary>
    /// <param name="options">评测选项</param>
    /// <returns>评测结果</returns>
    Task<MemoryBenchmarkResult> RunBenchmarkAsync(MemoryBenchmarkOptions options);

    /// <summary>
    /// 测试 CRUD 操作性能
    /// </summary>
    /// <param name="iterations">迭代次数</param>
    /// <returns>CRUD 性能结果</returns>
    Task<CrudPerformanceResult> TestCrudPerformanceAsync(int iterations);

    /// <summary>
    /// 测试检索操作性能
    /// </summary>
    /// <param name="queryCount">查询次数</param>
    /// <param name="memoryCount">记忆条目数量</param>
    /// <returns>检索性能结果</returns>
    Task<RetrievalPerformanceResult> TestRetrievalPerformanceAsync(int queryCount, int memoryCount);

    /// <summary>
    /// 测试遗忘机制性能
    /// </summary>
    /// <param name="memoryCount">记忆条目数量</param>
    /// <returns>遗忘性能结果</returns>
    Task<ForgettingPerformanceResult> TestForgettingPerformanceAsync(int memoryCount);
}

/// <summary>
/// Memory 评测选项
/// </summary>
public record MemoryBenchmarkOptions(
    int Iterations = 100,
    int MemoryCount = 1000,
    int QueryCount = 100,
    bool IncludeWarmup = true
);

/// <summary>
/// Memory 评测结果
/// </summary>
public record MemoryBenchmarkResult(
    CrudPerformanceResult? CrudPerformance,
    RetrievalPerformanceResult? RetrievalPerformance,
    ForgettingPerformanceResult? ForgettingPerformance,
    Dictionary<string, double> Metrics
);

/// <summary>
/// CRUD 性能结果
/// </summary>
public record CrudPerformanceResult(
    double CreateAvgMs,
    double ReadAvgMs,
    double UpdateAvgMs,
    double DeleteAvgMs,
    double TotalAvgMs
);

/// <summary>
/// 检索性能结果
/// </summary>
public record RetrievalPerformanceResult(
    double SearchAvgMs,
    double ListAvgMs,
    double FilterAvgMs,
    int ResultsPerQuery
);

/// <summary>
/// 遗忘性能结果
/// </summary>
public record ForgettingPerformanceResult(
    double ArchiveAvgMs,
    double DecayAvgMs,
    int ArchivedCount,
    int DecayedCount
);