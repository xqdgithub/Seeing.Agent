namespace Seeing.Agent.Tui.Services;

/// <summary>剪贴板文件。</summary>
public sealed record ClipboardFile(string Path, string MimeType, bool IsImage, long Size);

/// <summary>剪贴板图片（PNG 字节）。</summary>
public sealed record ClipboardImage(byte[] PngBytes, string FileName, long Size);

/// <summary>系统剪贴板读取端口。</summary>
public interface IClipboardReader
{
    /// <summary>当前平台是否支持读取文件/图片。</summary>
    bool IsSupported { get; }

    /// <summary>读取资源管理器复制的文件列表。</summary>
    IReadOnlyList<ClipboardFile> ReadFiles();

    /// <summary>读取文本；无文本返回 null。</summary>
    string? ReadText();

    /// <summary>读取剪贴板图片；无图片返回 null。</summary>
    Task<ClipboardImage?> ReadImageAsync(CancellationToken ct = default);
}

/// <summary>不支持剪贴板读取的平台。</summary>
public sealed class UnsupportedClipboardReader : IClipboardReader
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public IReadOnlyList<ClipboardFile> ReadFiles() => Array.Empty<ClipboardFile>();

    /// <inheritdoc />
    public string? ReadText() => null;

    /// <inheritdoc />
    public Task<ClipboardImage?> ReadImageAsync(CancellationToken ct = default)
        => Task.FromResult<ClipboardImage?>(null);
}
