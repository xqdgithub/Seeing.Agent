using Seeing.Session.Core;

// Alias to disambiguate from Seeing.TokenBudget.CompressionResult
using SessionCompressionResult = Seeing.Session.Core.CompressionResult;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// 压缩服务接口 - 执行消息压缩
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// 执行压缩
    /// </summary>
    /// <param name="session">会话数据</param>
    /// <param name="sessionConfig">会话级别配置（最高优先级）</param>
    /// <param name="agentConfig">Agent 级别配置（中等优先级）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>压缩结果（来自 Seeing.Session.Core）</returns>
    Task<SessionCompressionResult> CompressAsync(
        SessionData session,
        TokenBudgetConfig? sessionConfig = null,
        TokenBudgetConfig? agentConfig = null,
        CancellationToken cancellationToken = default);
}
