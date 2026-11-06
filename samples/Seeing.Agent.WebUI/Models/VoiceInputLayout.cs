namespace Seeing.Agent.WebUI.Models;

/// <summary>语音输入的传输布局与上限。</summary>
public static class VoiceInputLayout
{
    /// <summary>单段录音上限（约 10 分钟 opus）。</summary>
    public const long MaxAudioBytes = 25L * 1024 * 1024;

    /// <summary>单段录音最长时长（秒），到时自动停止。</summary>
    public const int MaxRecordingSeconds = 10 * 60;

    /// <summary>multipart 表单里的音频字段名。</summary>
    public const string ContainerFieldName = "audio";

    /// <summary>防空表单/CORS 简单请求的头名。</summary>
    public const string RequestHeaderName = "X-Seeing-Voice";

    /// <summary>上述头的期望值。</summary>
    public const string RequestHeaderValue = "1";
}

/// <summary>JS 返回的 textarea 选区锚点。</summary>
public sealed class VoiceInsertAnchor
{
    /// <summary>选区起点。</summary>
    public int Start { get; set; }

    /// <summary>选区终点。</summary>
    public int End { get; set; }
}

/// <summary>JS 启动录音的返回值。</summary>
public sealed class VoiceStartResult
{
    /// <summary>是否成功开始录音。</summary>
    public bool Ok { get; set; }

    /// <summary>失败代码（insecureContext / unsupportedBrowser / permissionDenied / noDevice / alreadyRecording）。</summary>
    public string? Code { get; set; }

    /// <summary>失败时的可读原因。</summary>
    public string? Message { get; set; }

    /// <summary>实际协商到的音频 MIME 类型。</summary>
    public string? MimeType { get; set; }
}
