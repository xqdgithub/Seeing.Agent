namespace Seeing.Agent.Core.Instructions;

internal static class ProjectInstructions
{
    public const string Tag = "project-instructions";
    public const string FingerprintMetadataKey = "instruction.fingerprints";

    internal static class MetadataKeys
    {
        public const string ProjectInstructions = "projectInstructions";
        public const string Reason = "instructionReason";
        public const string Cwd = "instructionCwd";
        public const string Paths = "instructionPaths";
    }

    internal static class Reasons
    {
        public const string Initial = "initial";
        public const string CwdChange = "cwd-change";
        public const string ContentChange = "content-change";
        public const string None = "none";
    }
}
