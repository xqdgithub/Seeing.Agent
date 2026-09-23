namespace Seeing.Agent.Core.Services
{
    /// <summary>
    /// 会话标题确保服务：在首条用户消息后生成简洁标题。
    /// </summary>
    public interface ISessionTitleService
    {
        /// <summary>若会话尚无标题，则基于首条用户消息生成并返回标题；否则返回 null。</summary>
        Task<string?> TryEnsureAsync(
            string sessionId,
            string userMessage,
            string? fallbackModelId,
            CancellationToken cancellationToken = default);
    }
}
