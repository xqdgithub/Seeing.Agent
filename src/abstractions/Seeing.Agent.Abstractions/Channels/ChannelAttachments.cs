namespace Seeing.Agent.Abstractions.Channels;

// ── ChannelAttachments.cs ──
public enum OutboundAttachmentKind { Image, Video, Audio, File }

/// <summary>出站附件：只描述事实，不描述平台。一期来源仅限会话工作区内的本地文件。</summary>
public sealed record OutboundAttachment
{
    public required OutboundAttachmentKind Kind { get; init; }

    /// <summary>本地绝对路径，已由生产端校验位于会话工作区内。</summary>
    public required string LocalPath { get; init; }

    /// <summary>文件字节数，由生产端读取。</summary>
    public required long SizeBytes { get; init; }

    /// <summary>展示名；null 时取路径文件名。</summary>
    public string? FileName { get; init; }
}

/// <summary>一条出站消息的内容：文本与附件至少一个非空。</summary>
public sealed record OutboundContent
{
    public string? Text { get; init; }

    public IReadOnlyList<OutboundAttachment> Attachments { get; init; } = [];
}
