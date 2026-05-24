namespace Seeing.Session.Compression
{
    /// <summary>
    /// 滑动窗口压缩策略 - 保留第一条和最后 N 条消息
    /// </summary>
    /// <remarks>
    /// 这是 SessionCompressor 的别名类，用于更清晰的命名
    /// </remarks>
    public class SlidingWindowCompression : SessionCompressor
    {
        /// <summary>
        /// 创建滑动窗口压缩策略
        /// </summary>
        /// <param name="keepLastN">保留最后 N 条消息（默认 20）</param>
        public SlidingWindowCompression(int keepLastN = 20) : base(keepLastN)
        {
        }
    }
}