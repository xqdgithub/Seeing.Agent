namespace Seeing.Session.Core;

/// <summary>
/// Session.Metadata 中由框架约定的键名。
/// </summary>
public static class SessionMetadataKeys
{
    /// <summary>
    /// 项目指令渐进加载的指纹快照（JSON：cwd + files）。
    /// 清空消息时应移除；Fork / Branch 时应随消息一并复制。
    /// </summary>
    public const string InstructionFingerprints = "instruction.fingerprints";

    /// <summary>
    /// 上下文源基线（JSON：状态版本 + 待处理重置位 + 各源已渲染文本与规范化值）。
    /// 清空消息时移除；Fork 不复制（分支属新 epoch）。
    /// </summary>
    public const string ContextBaseline = "context.baseline";

    /// <summary>
    /// 上下文管道增量消息的元数据键：本次叙述涉及的源键（<c>string[]</c>，稳定顺序）。
    /// 消费方据它判定「机器注入而非用户输入」（标题计数、UI 折叠）。
    /// </summary>
    public const string ContextUpdateSources = "context.update.sources";

    /// <summary>
    /// 子代理会话关联的父会话工具调用 ID（task 工具写入，UI 据此精确匹配父子关联）。
    /// </summary>
    public const string OriginToolCallId = "origin_tool_call_id";

    /// <summary>
    /// 子会话最近一次执行失败的错误信息（ExecutionRunner 在 MarkSessionError 时写入；
    /// 仅作运行时错误真相源，不进入 Messages/LLM 历史）。成功续跑时清除。
    /// </summary>
    public const string LastError = "last_error";

    /// <summary>
    /// 归档前原始会话状态（数字字符串）。恢复时还原，避免丢失 Completed/Idle/Error 语义。
    /// </summary>
    public const string PreArchiveStatus = "archive.pre_status";
}
