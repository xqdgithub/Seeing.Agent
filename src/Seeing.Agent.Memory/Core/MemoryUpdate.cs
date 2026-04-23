using System;
using System.Collections.Generic;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 记忆更新数据模型，用于部分更新记忆条目。
/// </summary>
public record MemoryUpdate
{
    /// <summary>
    /// 更新的内容（可选）
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// 更新的标签列表（可选）
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// 更新的置信度（可选）
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// 更新的重要性（可选）
    /// </summary>
    public double? Importance { get; init; }

    /// <summary>
    /// 更新的有效起始时间（可选）
    /// </summary>
    public DateTimeOffset? ValidAt { get; init; }

    /// <summary>
    /// 更新的失效时间（可选）
    /// </summary>
    public DateTimeOffset? InvalidAt { get; init; }

    /// <summary>
    /// 更新的来源（可选）
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// 访问次数增量（可选）
    /// </summary>
    public int? AccessCountDelta { get; init; }
}