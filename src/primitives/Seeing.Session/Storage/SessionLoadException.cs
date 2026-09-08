namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话加载异常，当会话文件损坏或无法正确解析时抛出
    /// </summary>
    public class SessionLoadException : Exception
    {
        /// <summary>
        /// 会话ID
        /// </summary>
        public string? SessionId { get; }

        /// <summary>
        /// 文件路径
        /// </summary>
        public string? FilePath { get; }

        public SessionLoadException()
            : base("会话加载失败")
        {
        }

        public SessionLoadException(string message)
            : base(message)
        {
        }

        public SessionLoadException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public SessionLoadException(string sessionId, string filePath, string message)
            : base(message)
        {
            SessionId = sessionId;
            FilePath = filePath;
        }

        public SessionLoadException(string sessionId, string filePath, string message, Exception innerException)
            : base(message, innerException)
        {
            SessionId = sessionId;
            FilePath = filePath;
        }
    }
}