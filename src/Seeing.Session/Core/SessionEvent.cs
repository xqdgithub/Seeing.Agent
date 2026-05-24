namespace Seeing.Session.Core
{
    /// <summary>
    /// Represents a change or activity related to a session.
    /// </summary>
    public class SessionEvent
    {
        /// <summary>Session 的唯一标识</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>事件类型</summary>
        public SessionEventType Type { get; set; }

        /// <summary>与事件关联的会话数据（如果有）</summary>
        public SessionData? Data { get; set; }

        /// <summary>与事件关联的会话消息（如果有）</summary>
        public SessionMessage? Message { get; set; }
    }

    /// <summary>
    /// 支持的事件类型集合
    /// </summary>
    public enum SessionEventType
    {
        Created,
        Updated,
        Saved,
        Loaded,
        Destroyed,
        MessageAdded
    }
}
