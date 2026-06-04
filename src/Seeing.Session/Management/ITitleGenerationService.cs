namespace Seeing.Session.Management
{
    /// <summary>
    /// 会话标题生成服务接口
    /// </summary>
    /// <remarks>
    /// 参考 OpenCode 的 title agent 设计：
    /// - 使用小型模型生成标题
    /// - 仅在第一条用户消息后生成
    /// - 标题长度限制在 50 字符以内
    /// - 使用与用户消息相同的语言
    /// </remarks>
    public interface ITitleGenerationService
    {
        /// <summary>
        /// 为会话生成标题（不自动保存，由调用方设置）
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="userMessage">用户消息内容</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>生成的标题，如果生成失败返回 null</returns>
        Task<string?> GenerateTitleAsync(
            string sessionId,
            string userMessage,
            CancellationToken ct = default);

        /// <summary>
        /// 检查是否应该为会话生成标题
        /// </summary>
        /// <param name="sessionTitle">当前会话标题</param>
        /// <param name="parentSessionId">父会话 ID（如果是 Fork）</param>
        /// <param name="realUserMessageCount">真实用户消息数量</param>
        /// <returns>是否应该生成标题</returns>
        bool ShouldGenerateTitle(
            string sessionTitle,
            string? parentSessionId,
            int realUserMessageCount);

        /// <summary>
        /// 清理标题文本
        /// </summary>
        /// <param name="rawTitle">原始标题</param>
        /// <returns>清理后的标题</returns>
        string CleanTitle(string rawTitle);
    }
}
