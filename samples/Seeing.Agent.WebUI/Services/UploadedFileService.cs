using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Configuration;
using Seeing.Agent.Tools.Support;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>落盘结果。</summary>
public sealed record StoredUpload(
    AttachmentDeliveryKind Kind,
    string FileName,
    string SessionRelativePath,
    string PromptPath,
    long Size,
    string MimeType);

/// <summary>WebUI 统一上传落盘服务：粘贴、拖拽、文件选择器共用。</summary>
public sealed class UploadedFileService
{
    private readonly IFileWorkspaceFactory _workspaces;
    private readonly IWorkspaceProvider _project;

    public UploadedFileService(IFileWorkspaceFactory workspaces, IWorkspaceProvider project)
    {
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <summary>写入统一上传目录并返回 prompt 路径。</summary>
    public async Task<StoredUpload> StoreAsync(
        string sessionId,
        string originalName,
        string? contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var isImage = contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        var maxBytes = isImage ? PastedFileLayout.MaxImageBytes : PastedFileLayout.MaxFileBytes;
        if (content.Length > maxBytes)
            throw new InvalidOperationException($"文件超过上限：{content.Length} > {maxBytes}");

        var workspace = await _workspaces.ResolveAsync(sessionId, ct).ConfigureAwait(false);
        try
        {
            var fileName = string.IsNullOrWhiteSpace(originalName) ? "pasted" : Path.GetFileName(originalName);
            var suffix = Guid.NewGuid().ToString("N")[..4];
            var storedName = PastedFileLayout.BuildStoredFileName(fileName, DateTimeOffset.Now, suffix);
            var absolute = PastedFileLayout.BuildAbsolutePath(workspace.RootPath, sessionId, storedName);

            await workspace.WriteBytesAsync(absolute, content, ct).ConfigureAwait(false);

            var relative = Path.GetRelativePath(workspace.RootPath, absolute).Replace('\\', '/');
            var promptPath = PastedFileLayout.ToPromptPath(_project.GetProjectRoot(), absolute);

            return new StoredUpload(
                isImage ? AttachmentDeliveryKind.BinaryAttachment : AttachmentDeliveryKind.PathReference,
                fileName,
                relative,
                promptPath,
                content.Length,
                contentType ?? "application/octet-stream");
        }
        finally
        {
            _workspaces.Release(sessionId);
        }
    }

    /// <summary>路径引用 → UI chip。</summary>
    public async Task<AttachmentViewModel> CreatePathReferenceAsync(
        string sessionId,
        UploadedFileItem item,
        CancellationToken ct = default)
    {
        var workspace = await _workspaces.ResolveAsync(sessionId, ct).ConfigureAwait(false);
        try
        {
            var absolute = ResolveUploadPath(workspace.RootPath, sessionId, item.SessionRelativePath);
            return new AttachmentViewModel
            {
                Kind = AttachmentDeliveryKind.PathReference,
                FileName = item.FileName,
                FileSize = item.Size,
                FileType = item.MimeType,
                PromptPath = PastedFileLayout.ToPromptPath(_project.GetProjectRoot(), absolute),
                SessionRelativePath = item.SessionRelativePath,
                Status = AttachmentUploadStatus.Completed
            };
        }
        finally
        {
            _workspaces.Release(sessionId);
        }
    }

    /// <summary>图片 → 读回字节、删除临时文件、生成 Base64 附件。</summary>
    public async Task<AttachmentViewModel> CreateImageAttachmentAsync(
        string sessionId,
        UploadedFileItem item,
        CancellationToken ct = default)
    {
        var workspace = await _workspaces.ResolveAsync(sessionId, ct).ConfigureAwait(false);
        try
        {
            var absolute = ResolveUploadPath(workspace.RootPath, sessionId, item.SessionRelativePath);
            var bytes = await workspace.ReadBytesAsync(absolute, ct).ConfigureAwait(false);

            try
            {
                await workspace.DeleteAsync(absolute, ct).ConfigureAwait(false);
            }
            catch
            {
                // 临时图片删除失败不影响附件生成；.seeing 已忽略。
            }

            if (bytes.LongLength > PastedFileLayout.MaxImageBytes)
                throw new InvalidOperationException($"图片超过上限：{bytes.LongLength}");

            return new AttachmentViewModel
            {
                Kind = AttachmentDeliveryKind.BinaryAttachment,
                FileName = item.FileName,
                FileSize = bytes.LongLength,
                FileType = item.MimeType,
                Base64Content = $"data:{item.MimeType};base64,{Convert.ToBase64String(bytes)}",
                SessionRelativePath = item.SessionRelativePath,
                Status = AttachmentUploadStatus.Completed
            };
        }
        finally
        {
            _workspaces.Release(sessionId);
        }
    }

    /// <summary>删除未发送的路径引用文件。</summary>
    public async Task DeleteAsync(string sessionId, string sessionRelativePath, CancellationToken ct = default)
    {
        var workspace = await _workspaces.ResolveAsync(sessionId, ct).ConfigureAwait(false);
        try
        {
            var absolute = ResolveUploadPath(workspace.RootPath, sessionId, sessionRelativePath);
            try
            {
                await workspace.DeleteAsync(absolute, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        finally
        {
            _workspaces.Release(sessionId);
        }
    }

    /// <summary>把不可信的相对路径锚定到会话上传目录；越界抛异常。</summary>
    public static string ResolveUploadPath(string sessionRoot, string sessionId, string sessionRelativePath)
    {
        if (string.IsNullOrWhiteSpace(sessionRelativePath))
            throw new InvalidOperationException("上传路径为空");
        if (Path.IsPathRooted(sessionRelativePath))
            throw new InvalidOperationException("上传路径不能是绝对路径");
        if (sessionRelativePath.Any(char.IsControl))
            throw new InvalidOperationException("上传路径包含控制字符");

        foreach (var segment in sessionRelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
                throw new InvalidOperationException("上传路径不能包含 ..");
        }

        var root = Path.GetFullPath(sessionRoot);
        var uploadsRoot = Path.GetFullPath(
            Path.Combine(root, ".seeing", "uploads", PastedFileLayout.SafeSessionId(sessionId)));
        var full = Path.GetFullPath(Path.Combine(root, sessionRelativePath));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = uploadsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, comparison))
            throw new InvalidOperationException("上传路径越出会话上传目录");

        return full;
    }
}
