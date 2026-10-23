using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using System.Text.Json;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 附件归一化结果：成功携带 <see cref="OutboundAttachment"/>；失败携带可读原因。
/// <para>
/// <see cref="IsParameterError"/> 为 <see langword="true"/> 表示调用方参数不自洽（工具返回「参数错误」）；
/// 为 <see langword="false"/> 且 <see cref="Attachment"/> 为空表示文件不可用（AttachmentUnavailable）。
/// </para>
/// </summary>
internal sealed record NormalizedAttachment
{
    public OutboundAttachment? Attachment { get; init; }

    public string? Error { get; init; }

    public bool IsParameterError { get; init; }

    public static NormalizedAttachment Ok(OutboundAttachment attachment) => new() { Attachment = attachment };

    public static NormalizedAttachment Invalid(string error) =>
        new() { Error = error, IsParameterError = true };

    public static NormalizedAttachment Unavailable(string detail) => new() { Error = detail };
}

/// <summary>
/// 把工具参数里的附件描述（路径 + 可选类型/展示名）归一化为出站附件事实。
/// <para>
/// 只做路径与文件事实校验（工作区边界、存在性、非目录、非空），不做类型/数量/大小上限判定——
/// 唯一权威是 Comms（<c>OutboundDeliveryService</c>）。
/// </para>
/// </summary>
internal sealed class ChannelAttachmentNormalizer
{
    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };

    private static readonly HashSet<string> s_videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm"
    };

    private static readonly HashSet<string> s_audioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".amr", ".silk", ".m4a", ".ogg", ".flac"
    };

    private readonly IWorkspacePathGate _pathGate;
    private readonly ISessionWorkspaceResolver _sessionWorkspace;
    private readonly IWorkspaceProvider _workspace;
    private readonly IFileSystem _fileSystem;

    public ChannelAttachmentNormalizer(
        IWorkspacePathGate pathGate,
        ISessionWorkspaceResolver sessionWorkspace,
        IWorkspaceProvider workspace,
        IFileSystem fileSystem)
    {
        _pathGate = pathGate;
        _sessionWorkspace = sessionWorkspace;
        _workspace = workspace;
        _fileSystem = fileSystem;
    }

    /// <summary>归一化单个附件；<paramref name="attachment"/> 为 <c>attachments[]</c> 的一项。</summary>
    public NormalizedAttachment Normalize(string sessionId, JsonElement attachment)
    {
        if (attachment.ValueKind != JsonValueKind.Object)
            return NormalizedAttachment.Invalid("attachments 的每一项必须是对象");

        var path = ToolJson.ReadString(attachment, "path");
        if (path == null)
            return NormalizedAttachment.Invalid("attachments[].path 不能为空");

        if (!TryResolveKind(attachment, path, out var kind, out var kindError))
            return NormalizedAttachment.Invalid(kindError);

        string candidate;
        if (Path.IsPathRooted(path))
        {
            // 绝对路径不需要基准目录。
            candidate = path;
        }
        else
        {
            // 基准目录 = 会话 cwd ?? 项目根。
            var baseDirectory = _sessionWorkspace.ResolveWorkingDirectory(sessionId)
                ?? _workspace.GetProjectRoot();
            if (string.IsNullOrWhiteSpace(baseDirectory))
                return NormalizedAttachment.Invalid("缺少工作区，请提供绝对路径");
            candidate = Path.Combine(baseDirectory, path);
        }

        var fullPath = _fileSystem.GetFullPath(candidate);

        // 严格工作区包含（无条件，不受 RestrictToWorkspace / 授权扩权影响）。
        var gateFailure = _pathGate.EnsureWithinWorkspace(sessionId, fullPath);
        if (gateFailure != null)
            return NormalizedAttachment.Unavailable(gateFailure);

        if (!_fileSystem.Exists(fullPath))
            return NormalizedAttachment.Unavailable($"文件不存在：{fullPath}");

        if (_fileSystem.IsDirectory(fullPath))
            return NormalizedAttachment.Unavailable($"路径是目录：{fullPath}");

        long size;
        try
        {
            size = _fileSystem.GetFileLength(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or NotSupportedException)
        {
            return NormalizedAttachment.Unavailable($"无法读取文件：{fullPath}（{ex.Message}）");
        }

        if (size <= 0)
            return NormalizedAttachment.Unavailable($"文件为空：{fullPath}");

        var name = ToolJson.ReadString(attachment, "name") ?? Path.GetFileName(fullPath);

        return NormalizedAttachment.Ok(new OutboundAttachment
        {
            Kind = kind,
            LocalPath = fullPath,
            SizeBytes = size,
            FileName = string.IsNullOrWhiteSpace(name) ? null : name
        });
    }

    private static bool TryResolveKind(
        JsonElement attachment, string path, out OutboundAttachmentKind kind, out string error)
    {
        var explicitKind = ToolJson.ReadString(attachment, "kind");
        if (explicitKind == null)
        {
            kind = InferKind(path);
            error = string.Empty;
            return true;
        }

        switch (explicitKind.ToLowerInvariant())
        {
            case "image":
                kind = OutboundAttachmentKind.Image;
                error = string.Empty;
                return true;
            case "video":
                kind = OutboundAttachmentKind.Video;
                error = string.Empty;
                return true;
            case "audio":
                kind = OutboundAttachmentKind.Audio;
                error = string.Empty;
                return true;
            case "file":
                kind = OutboundAttachmentKind.File;
                error = string.Empty;
                return true;
            default:
                kind = default;
                error = "attachments[].kind 必须是 image、video、audio 或 file";
                return false;
        }
    }

    private static OutboundAttachmentKind InferKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (s_imageExtensions.Contains(extension))
            return OutboundAttachmentKind.Image;
        if (s_videoExtensions.Contains(extension))
            return OutboundAttachmentKind.Video;
        if (s_audioExtensions.Contains(extension))
            return OutboundAttachmentKind.Audio;
        return OutboundAttachmentKind.File;
    }
}
