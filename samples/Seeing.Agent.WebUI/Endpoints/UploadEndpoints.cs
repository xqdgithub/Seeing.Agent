using Seeing.Agent.Tools.Support;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Endpoints;

/// <summary>粘贴/拖拽上传端点。</summary>
public static class UploadEndpoints
{
    /// <summary>上传路由。</summary>
    public const string Route = "/api/uploads";

    /// <summary>映射上传端点。</summary>
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        string sessionId,
        UploadedFileService service,
        ISessionManager sessions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessions.Get(sessionId) is null)
            return Results.NotFound(new { error = "session not found" });

        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart form required" });

        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count == 0 || form.Files.Count > PastedFileLayout.MaxFilesPerRequest)
            return Results.BadRequest(new { error = "invalid file count" });

        var items = new List<UploadedFileItem>(form.Files.Count);
        foreach (var file in form.Files)
        {
            var mimeType = ResolveMimeType(file);
            var maxBytes = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? PastedFileLayout.MaxImageBytes
                : PastedFileLayout.MaxFileBytes;

            if (file.Length <= 0 || file.Length > maxBytes)
                return Results.BadRequest(new { error = $"invalid file size: {file.FileName}" });

            await using var source = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            var content = buffer.ToArray();
            if (content.Length > maxBytes)
                return Results.BadRequest(new { error = $"invalid file size: {file.FileName}" });

            var stored = await service.StoreAsync(sessionId, file.FileName, mimeType, content, ct);
            items.Add(new UploadedFileItem(
                stored.Kind == AttachmentDeliveryKind.BinaryAttachment ? "image" : "file",
                stored.FileName,
                stored.SessionRelativePath,
                stored.Size,
                stored.MimeType));
        }

        return Results.Json(new { items });
    }

    private static string ResolveMimeType(IFormFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType)
            && !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return file.ContentType;

        return FileContentSupport.GetMimeType(file.FileName);
    }
}
