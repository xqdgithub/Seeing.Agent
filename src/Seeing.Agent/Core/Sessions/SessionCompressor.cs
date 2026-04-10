using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Sessions;

/// <summary>
/// 会话压缩器 - 使用滑动窗口算法压缩会话历史
/// </summary>
/// <remarks>
/// Sliding Window 规则：
/// 1. 保留第一条消息（系统提示）
/// 2. 保留最后 N 条消息（默认 20）
/// 3. 丢弃中间消息
/// </remarks>
public class SessionCompressor
{
    private readonly int _keepLastN;

    /// <summary>
    /// 创建会话压缩器
    /// </summary>
    /// <param name="keepLastN">保留最后 N 条消息（默认 20）</param>
    public SessionCompressor(int keepLastN = 20)
    {
        _keepLastN = keepLastN;
    }

    /// <summary>
    /// 压缩会话消息（Sliding Window）
    /// </summary>
    /// <param name="messages">原始消息列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>压缩后的消息列表</returns>
    public Task<List<ChatMessage>> CompressAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        // 如果消息数量不足以压缩，直接返回
        if (messages.Count <= _keepLastN + 1)
        {
            return Task.FromResult(messages.ToList());
        }

        var result = new List<ChatMessage>();
        
        // 保留第一条消息（系统提示）
        var firstMessage = messages[0];
        result.Add(firstMessage);
        
        // 保留最后 N 条消息
        var startIndex = messages.Count - _keepLastN;
        for (var i = startIndex; i < messages.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            result.Add(messages[i]);
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// 计算压缩后保留的消息数量
    /// </summary>
    /// <param name="messageCount">原始消息数量</param>
    /// <returns>压缩后保留的消息数量</returns>
    public int GetRetainedCount(int messageCount)
    {
        if (messageCount <= _keepLastN + 1)
        {
            return messageCount;
        }
        
        // 第一条 + 最后 N 条
        return 1 + _keepLastN;
    }

    /// <summary>
    /// 计算将被丢弃的消息数量
    /// </summary>
    /// <param name="messageCount">原始消息数量</param>
    /// <returns>将被丢弃的消息数量</returns>
    public int GetDiscardedCount(int messageCount)
    {
        return messageCount - GetRetainedCount(messageCount);
    }
}