namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>编辑器命令执行结果。</summary>
public enum CommandOutcome { Ok, NoEditor, Failed }

/// <summary>宿主请求的编辑器侧结果（是否可用 + JSON 载荷）。</summary>
public readonly record struct HostResponsePayload(bool Available, string? Json)
{
    /// <summary>编辑器不可用（未打开/只读/非文本等）。</summary>
    public static HostResponsePayload Unavailable => new(false, null);

    /// <summary>请求成功；可选携带 JSON 载荷。</summary>
    public static HostResponsePayload Ok(string? json = null) => new(true, json);
}
