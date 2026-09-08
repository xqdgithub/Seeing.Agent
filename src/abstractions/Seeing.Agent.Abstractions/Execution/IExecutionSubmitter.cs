using Seeing.Agent.Abstractions.Models;

namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 执行提交口 - 用于提交、取消与等待后台 Agent 执行
/// </summary>
public interface IExecutionSubmitter
{
    /// <summary>
    /// 提交执行请求（非阻塞）。
    /// </summary>
    Task<ExecutionSubmitResult> SubmitAsync(
        string sessionId,
        ChatInput input,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消指定执行。
    /// </summary>
    Task<bool> CancelAsync(string executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消会话及其子会话下未终态的执行。
    /// </summary>
    Task<int> CancelBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 等待指定执行进入终态。
    /// </summary>
    Task WaitForExecutionAsync(string executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消进程内全部未终态执行（热重载强制切换等）。
    /// </summary>
    Task<int> CancelAllInFlightAsync(CancellationToken cancellationToken = default);
}
