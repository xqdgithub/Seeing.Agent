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
    /// 子代理会话关联的父会话工具调用 ID（task 工具写入，UI 据此精确匹配父子关联）。
    /// </summary>
    public const string OriginToolCallId = "origin_tool_call_id";
}
