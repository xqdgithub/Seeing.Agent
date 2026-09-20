using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.Abstractions.Questions;

/// <summary>问答在途请求管理器契约（登记/等待/幂等完成/收敛）。</summary>
public interface IQuestionRequestManager : IPendingRequestManager<QuestionRequest, QuestionResult>
{
}
