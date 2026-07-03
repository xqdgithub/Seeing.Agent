namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 相关 Session Metadata 键名。
/// </summary>
public static class AcpMetadataKeys
{
    public const string PassthroughPrefix = "acp:passthrough:";
    public const string TaskPrefix = "acp:task:";

    public static string Passthrough(string seeingSessionId) => PassthroughPrefix + seeingSessionId;

    public static string Task(string taskId) => TaskPrefix + taskId;
}
