namespace Seeing.Agent.Tools.Support.Speech;

/// <summary>
/// 音频文件格式与上限：扩展名 → MIME 映射、单文件字节上限。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不复用 <see cref="FileContentSupport.GetMimeType"/></b>：那张表把
/// <c>.mp3</c>/<c>.wav</c> 归入二进制扩展名，且没有音频 MIME 映射，未知扩展名一律回落
/// <c>application/octet-stream</c>。改它会牵动上传链路（<c>UploadEndpoints</c>、
/// <c>FileWorkspace</c>）的既有行为，故单列一张音频表，零回归面。
/// </para>
/// <para>
/// <b>上限值同源</b>：与 WebUI 的 <c>VoiceInputLayout.MaxAudioBytes</c> 取同一数值（OpenAI ASR
/// 的 25MB 上限）。能力包不能引用 samples 层，故各持一份；改一处时另一处要同步。
/// </para>
/// </remarks>
public static class AudioFormatSupport
{
    /// <summary>单段音频上限（25MB，与 OpenAI ASR 端点一致）。</summary>
    public const long MaxAudioBytes = 25L * 1024 * 1024;

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "audio/mp4",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/opus",
        [".webm"] = "audio/webm",
        [".flac"] = "audio/flac",
        [".aac"] = "audio/aac",
        [".amr"] = "audio/amr",
        [".wma"] = "audio/x-ms-wma",
        [".silk"] = "audio/silk",
        [".slk"] = "audio/silk",
        [".3gp"] = "audio/3gpp",
    };

    /// <summary>按扩展名取音频 MIME；不是已知音频扩展名时返回 <see langword="false"/>。</summary>
    public static bool TryGetMimeType(string? filePath, out string mimeType)
    {
        mimeType = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (!MimeTypes.TryGetValue(Path.GetExtension(filePath), out var resolved))
            return false;

        mimeType = resolved;
        return true;
    }

    /// <summary>按扩展名判定是否受支持的音频文件。</summary>
    public static bool IsSupported(string? filePath)
        => TryGetMimeType(filePath, out _);

    /// <summary>受支持音频扩展名（升序，用于报错时列出）。</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        MimeTypes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
}
