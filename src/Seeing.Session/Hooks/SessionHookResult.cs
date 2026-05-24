namespace Seeing.Session.Hooks
{
    /// <summary>
    /// Session Hook 执行结果
    /// </summary>
    public class SessionHookResult
    {
        /// <summary>是否继续执行后续 Hook</summary>
        public bool Continue { get; set; } = true;

        /// <summary>错误信息</summary>
        public Exception? Error { get; set; }
    }
}