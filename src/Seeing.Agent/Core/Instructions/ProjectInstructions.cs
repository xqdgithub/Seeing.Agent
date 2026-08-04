using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 项目指令注入消息约定（标签、Metadata 键、reason）。
/// </summary>
public static class ProjectInstructions
{
    public const string Tag = "project-instructions";
    public const string FingerprintMetadataKey = SessionMetadataKeys.InstructionFingerprints;

    public static class MetadataKeys
    {
        public const string ProjectInstructions = "projectInstructions";
        public const string Reason = "instructionReason";
        public const string Cwd = "instructionCwd";
        public const string Paths = "instructionPaths";
    }

    public static class Reasons
    {
        public const string Initial = "initial";
        public const string CwdChange = "cwd-change";
        public const string ContentChange = "content-change";
        public const string None = "none";
    }
}
