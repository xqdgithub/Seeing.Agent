namespace Seeing.Agent.Services
{
    /// <summary>
    /// 会话标题确保服务：在首条用户消息后生成简洁标题。
    /// </summary>
    public interface ISessionTitleEnsuring
    {
        Task<string?> TryEnsureAsync(
            string sessionId,
            string userMessage,
            string? fallbackModelId,
            CancellationToken cancellationToken = default);
    }
}
