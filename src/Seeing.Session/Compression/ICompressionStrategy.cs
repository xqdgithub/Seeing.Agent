using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Session.Compression
{
    /// <summary>
    /// 消息压缩策略接口
    /// </summary>
    public interface ICompressionStrategy
    {
        /// <summary>策略名称</summary>
        string Name { get; }

        /// <summary>压缩消息列表</summary>
        IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages);

        /// <summary>估算压缩后保留的消息数量</summary>
        int EstimateRetainedCount(int messageCount);

        /// <summary>
        /// Compresses messages based on token budget constraints.
        /// </summary>
        /// <param name="messages">The messages to compress.</param>
        /// <param name="config">Token budget configuration.</param>
        /// <param name="tokenCounter">Token counter for estimating message sizes.</param>
        /// <returns>Result containing compression details and compressed messages.</returns>
        CompressionResult CompressByTokenBudget(
            IReadOnlyList<SessionMessage> messages,
            TokenBudgetConfig config,
            ITokenCounter tokenCounter);
    }
}