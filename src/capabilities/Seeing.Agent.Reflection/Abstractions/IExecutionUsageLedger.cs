using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Reflection.Abstractions;

/// <summary>
/// 单日复盘用量账本端口。
/// <para>
/// 只记账不复位：每日重置由跨天判断隐式完成，避免依赖外部定时器，
/// 进程长时间运行或反复重启都能得到一致的当日用量。
/// </para>
/// </summary>
public interface IExecutionUsageLedger
{
    /// <summary>返回今日（UTC）已消耗的 Token 总量。</summary>
    long UsedToday();

    /// <summary>累计一次执行的 Token 用量。</summary>
    void Add(TokenUsage usage);
}
