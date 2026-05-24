using Seeing.Session.Core;

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
    }
}