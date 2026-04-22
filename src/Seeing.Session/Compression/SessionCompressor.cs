using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Session.Core;

namespace Seeing.Session.Compression
{
    /// <summary>
    /// Session 压缩器 - 使用滑动窗口算法压缩会话历史
    /// </summary>
    /// <remarks>
    /// Sliding Window 规则：
    /// 1. 保留第一条消息（系统提示）
    /// 2. 保留最后 N 条消息（默认 20）
    /// 3. 丢弃中间消息
    /// </remarks>
    public class SessionCompressor : ICompressionStrategy
    {
        private readonly int _keepLastN;

        /// <summary>
        /// 暴露给外部的策略名称
        /// </summary>
        public string Name => "SlidingWindow";

        /// <summary>
        /// 创建会话压缩器
        /// </summary>
        /// <param name="keepLastN">保留最后 N 条消息（默认 20）</param>
        public SessionCompressor(int keepLastN = 20)
        {
            _keepLastN = keepLastN;
        }

        /// <summary>
        /// 压缩会话消息（Sliding Window，同步实现）
        /// </summary>
        /// <param name="messages">原始消息列表</param>
        /// <returns>压缩后的消息列表</returns>
        public IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages)
        {
            // 如果消息数量不足以压缩，直接返回原始消息的只读副本
            if (messages.Count <= _keepLastN + 1)
            {
                return messages;
            }

            var result = new List<SessionMessage>();

            // 保留第一条消息（系统提示）
            var firstMessage = messages[0];
            result.Add(firstMessage);

            // 保留最后 N 条消息
            var startIndex = messages.Count - _keepLastN;
            for (var i = startIndex; i < messages.Count; i++)
            {
                result.Add(messages[i]);
            }

            return result;
        }

        /// <summary>
        /// 兼容异步入口：将同步压缩结果包装成 Task<List<SessionMessage>>
        /// </summary>
        public Task<List<SessionMessage>> CompressAsync(
            IReadOnlyList<SessionMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var list = Compress(messages);
            return Task.FromResult(new List<SessionMessage>(list));
        }

        /// <summary>
        /// 估算压缩后将保留的消息数量（对外接口暴露）
        /// </summary>
        public int EstimateRetainedCount(int messageCount) => GetRetainedCount(messageCount);

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
}
