namespace Seeing.Agent.Abstractions.Tools
{
    /// <summary>
    /// task 工具结果的预定义元数据键。
    /// <para>
    /// <c>TaskTool</c> 在 <see cref="ToolResult.Metadata"/> 中发出这些键；
    /// 服务端 <c>ChatEventTracker</c> 据此映射到 <c>SessionToolCall.Task*</c>，
    /// 各宿主（WebUI/TUI/Gateway）共享同一权威来源。
    /// </para>
    /// </summary>
    public static class TaskMetadataKeys
    {
        /// <summary>子会话 ID（≡ task_id）。</summary>
        public const string TaskId = "task_id";

        /// <summary>子代理类型（subagent_type）。</summary>
        public const string TaskAgent = "task_agent";

        /// <summary>任务简短描述。</summary>
        public const string TaskDescription = "task_description";

        /// <summary>是否后台任务（bool）。</summary>
        public const string TaskBackground = "task_background";
    }
}
