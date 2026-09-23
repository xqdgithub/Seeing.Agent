using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 项目指令注入消息约定（标签、Metadata 键、reason）。
/// </summary>
public static class ProjectInstructions
{
    /// <summary>指令注入消息的标签</summary>
    public const string Tag = "project-instructions";
    /// <summary>存放指纹快照的会话 Metadata 键</summary>
    public const string FingerprintMetadataKey = SessionMetadataKeys.InstructionFingerprints;

    /// <summary>指令注入消息的 Metadata 键集合</summary>
    public static class MetadataKeys
    {
        /// <summary>指令内容键</summary>
        public const string ProjectInstructions = "projectInstructions";
        /// <summary>注入原因键</summary>
        public const string Reason = "instructionReason";
        /// <summary>采集时工作目录键</summary>
        public const string Cwd = "instructionCwd";
        /// <summary>注入文件路径列表键</summary>
        public const string Paths = "instructionPaths";
    }

    /// <summary>注入触发原因取值集合</summary>
    public static class Reasons
    {
        /// <summary>首次注入</summary>
        public const string Initial = "initial";
        /// <summary>工作目录变更导致重新注入</summary>
        public const string CwdChange = "cwd-change";
        /// <summary>指令文件内容变更导致更新注入</summary>
        public const string ContentChange = "content-change";
        /// <summary>无变更、未注入</summary>
        public const string None = "none";
    }
}
