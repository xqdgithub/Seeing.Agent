using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>单个会话的运行时状态。</summary>
internal sealed class SessionExecutionState
{
    public ExecutionRecord? CurrentExecution { get; set; }   // 运行中的当前轮（空闲为 null）
    public ExecutionRecord? LastExecution { get; set; }      // 最近一轮（终态后保留）
    public int PendingInputCount { get; set; }               // 未消费输入数
}
