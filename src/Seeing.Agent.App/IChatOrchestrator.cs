using Seeing.Agent.Core.Events;
using Seeing.Agent.App.Models;
using Seeing.Agent.App.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.App;

/// <summary>
/// 聊天编排器 - 统一的 Agent 执行入口
/// <para>
/// 提供最小化的入口参数，内部管理 Session 生命周期、命令预处理、Agent 执行、事件发送。
/// WebUI 应该只通过此接口读写 Session，不直接调用 SessionManager。
/// </para>
/// </summary>
public interface IChatOrchestrator
{
    #region 执行管理（新接口）

    /// <summary>
    /// 提交执行请求（非阻塞）。
    /// 用户消息会立即保存，执行在后台进行。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="input">用户输入（文本 + 附件）</param>
    /// <param name="options">可选配置（agent、model、mode 等）</param>
    /// <returns>提交结果，包含执行 ID 和状态</returns>
    Task<ExecutionSubmitResult> SubmitAsync(string sessionId, ChatInput input, ChatOptions? options = null);

    /// <summary>
    /// 取消执行
    /// </summary>
    /// <param name="executionId">执行 ID</param>
    /// <returns>是否成功取消</returns>
    bool Cancel(string executionId);

    /// <summary>
    /// 获取会话执行状态概览
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>执行状态概览</returns>
    SessionExecutionOverview GetOverview(string sessionId);

    /// <summary>
    /// 获取单个执行记录
    /// </summary>
    /// <param name="executionId">执行 ID</param>
    /// <returns>执行记录，如果不存在则返回 null</returns>
    ExecutionRecord? GetExecution(string executionId);

    /// <summary>
    /// 获取缓冲的事件（用于断线重连）
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>缓冲的事件列表</returns>
    IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId);

    /// <summary>
    /// 订阅执行事件流（实时更新）
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>消息事件流</returns>
    IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken cancellationToken = default);

    #endregion

    #region Session 读取方法（供 WebUI 只读访问）

    /// <summary>
    /// 获取指定会话（如果不存在则返回 null）
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>SessionData 或 null</returns>
    Task<SessionData?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 确保会话存在（如果不存在则创建新会话）
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>SessionData</returns>
    Task<SessionData> EnsureSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有会话列表
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>会话列表</returns>
    Task<IReadOnlyList<SessionData>> ListSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新会话
    /// </summary>
    /// <param name="title">可选标题</param>
    /// <param name="agentId">可选 Agent ID</param>
    /// <param name="workingDirectory">可选工作目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>新创建的 SessionData</returns>
    Task<SessionData> CreateSessionAsync(string? title = null, string? agentId = null, string? workingDirectory = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除会话
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重命名会话
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="newTitle">新标题</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task RenameSessionAsync(string sessionId, string newTitle, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分支会话（创建当前会话的副本）
    /// </summary>
    /// <param name="sessionId">源会话 ID</param>
    /// <param name="title">新会话标题（可选）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>分支后的新会话</returns>
    Task<SessionData> BranchSessionAsync(string sessionId, string? title = null, CancellationToken cancellationToken = default);

    #endregion
}
