namespace Seeing.Agent.Execution;

/// <summary>
/// 执行状态查询接口 - 用于查询后台任务的执行状态
/// </summary>
public interface IExecutionStatusProvider
{
    /// <summary>
    /// 获取会话的执行状态概览
    /// </summary>
    SessionExecutionOverview GetOverview(string sessionId);

    /// <summary>
    /// 获取指定执行记录
    /// </summary>
    ExecutionRecord? GetExecution(string executionId);
}
