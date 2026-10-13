namespace Seeing.Session.Persistence
{
    /// <summary>
    /// 会话归档配置：启动扫描自动归档不活跃会话。
    /// </summary>
    public sealed class SessionArchivalOptions
    {
        /// <summary>是否启用启动扫描归档（默认 true）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 不活跃阈值：<c>LastActiveAt</c> 早于 <c>now - 阈值</c> 视为不活跃（默认 30 天）。
        /// </summary>
        public TimeSpan InactivityThreshold { get; set; } = TimeSpan.FromDays(30);

        /// <summary>消息数下限：低于此值不归档（避免归档空会话；0 = 不限制）。</summary>
        public int MinMessageCount { get; set; }

        /// <summary>启动时扫描（预留扩展点，本次恒为 true）。</summary>
        public bool ScanOnStartup { get; set; } = true;
    }
}
