namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 配额管理接口 - 每日/每月限额
/// </summary>
public interface IQuotaManager
{
    /// <summary>检查是否超出配额</summary>
    Task<bool> IsQuotaAvailableAsync(string quotaType = "daily", CancellationToken ct = default);

    /// <summary>获取当前使用量</summary>
    Task<QuotaUsage> GetUsageAsync(string quotaType = "daily", CancellationToken ct = default);

    /// <summary>设置配额限制</summary>
    Task SetLimitAsync(string quotaType, long limit, CancellationToken ct = default);

    /// <summary>记录消耗</summary>
    Task ConsumeAsync(long tokens, string quotaType = "daily", CancellationToken ct = default);
}

/// <summary>
/// 配额使用情况
/// </summary>
public record QuotaUsage(
    string Type,
    long Used,
    long Limit,
    double UsageRate,
    DateTimeOffset ResetAt
);
