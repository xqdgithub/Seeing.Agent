namespace Seeing.Agent.Core.Questions;

/// <summary>
/// 问题管理器接口 - 支持 Agent 向用户提问并等待回复
/// </summary>
public interface IQuestionManager
{
    /// <summary>
    /// 提问并等待回答
    /// </summary>
    /// <param name="request">问题请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>问题回答结果</returns>
    Task<QuestionResult> AskAsync(QuestionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交回答
    /// </summary>
    /// <param name="requestId">请求 ID</param>
    /// <param name="answers">回答列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task AnswerAsync(string requestId, List<QuestionAnswer> answers, CancellationToken cancellationToken = default);

    /// <summary>
    /// 拒绝回答（用户取消）
    /// </summary>
    /// <param name="requestId">请求 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task RejectAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有待处理的问题请求
    /// </summary>
    /// <returns>待处理的请求列表</returns>
    Task<IReadOnlyList<QuestionRequest>> GetPendingAsync();
}

/// <summary>
/// 用户拒绝回答异常
/// </summary>
public class QuestionRejectedException : Exception
{
    /// <summary>
    /// 创建拒绝异常
    /// </summary>
    public QuestionRejectedException() : base("用户取消了此问题") { }

    /// <summary>
    /// 创建拒绝异常
    /// </summary>
    /// <param name="message">异常消息</param>
    public QuestionRejectedException(string message) : base(message) { }
}