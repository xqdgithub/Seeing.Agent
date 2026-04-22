namespace Seeing.Session.Hooks
{
    /// <summary>
    /// Session Hook 点常量定义
    /// </summary>
    public static class SessionHookPoints
    {
        /// <summary>Session 创建时触发</summary>
        public const string Created = "session.created";
        
        /// <summary>Session 保存前触发</summary>
        public const string Saving = "session.saving";
        
        /// <summary>Session 保存后触发</summary>
        public const string Saved = "session.saved";
        
        /// <summary>Session 加载前触发</summary>
        public const string Loading = "session.loading";
        
        /// <summary>Session 加载后触发</summary>
        public const string Loaded = "session.loaded";
        
        /// <summary>Session 销毁时触发</summary>
        public const string Destroyed = "session.destroyed";

        /// <summary>Session 消息已添加触发</summary>
        public const string MessageAdded = "session.message_added";

        /// <summary>Session 压缩完成触发</summary>
        public const string Compressed = "session.compressed";
    }
}
