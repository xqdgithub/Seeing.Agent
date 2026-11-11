using Microsoft.AspNetCore.Mvc;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Output;
using Seeing.Agent.Paths;
using Seeing.Agent.Tools.Support;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Endpoints;

/// <summary>
/// 会话资源取值端点：把会话目录里的图片/文件交给浏览器加载。
/// <para>
/// <b>为什么需要它</b>：工具与用户附件改为「只存 <c>LocalPath</c>」后，base64 不再随消息下发。
/// UI 若把 base64 塞进 render batch，会撞上 Blazor Server 的
/// <c>MaximumReceiveMessageSize</c>（默认 32KB）——本项目已在粘贴图片功能上踩过一次。
/// 走 <c>&lt;img src&gt;</c> 由浏览器并行加载 + 缓存 + 懒加载，Blazor 只传 URL 字符串。
/// </para>
/// <para>
/// <b>安全</b>：会话必须存在、路径必须落在该会话自己的 ref / uploads 目录内（<see cref="PathBoundary"/>）、
/// 扩展名必须在图片白名单内。越界一律 404（不返回 403，避免暴露路径存在性）。
/// </para>
/// </summary>
public static class SessionAssetEndpoints
{
    /// <summary>资源路由模板。</summary>
    public const string Route = "/api/sessions/{sessionId}/assets/{scope}/{**relativePath}";

    /// <summary>工具产出（<c>{sessionId}.ref/</c>）。</summary>
    public const string ScopeRef = "ref";

    /// <summary>用户附件（<c>{sessionRoot}/.seeing/uploads/{sessionId}/</c>）。</summary>
    public const string ScopeUploads = "uploads";

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    /// <summary>映射资源端点。</summary>
    public static IEndpointRouteBuilder MapSessionAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string sessionId,
        string scope,
        string relativePath,
        ISessionManager sessions,
        IToolOutputStore outputStore,
        IWorkspaceProvider project,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessions.Get(sessionId) is null)
            return Results.NotFound();

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Any(char.IsControl))
            return Results.NotFound();

        // 扩展名白名单：本端点只服务图片，其它类型一律不可达。
        if (!s_imageExtensions.Contains(Path.GetExtension(relativePath)))
            return Results.NotFound();

        var rootDirectory = ResolveRoot(scope, sessionId, outputStore, project);
        if (rootDirectory is null)
            return Results.NotFound();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        }
        catch
        {
            return Results.NotFound();
        }

        // 硬边界：符号链接解析后仍必须在本会话目录内（PathBoundary 是唯一实现）
        if (!PathBoundary.IsStrictlyWithin(fullPath, rootDirectory))
            return Results.NotFound();

        if (!File.Exists(fullPath))
            return Results.NotFound();

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(fullPath);
        }
        catch
        {
            return Results.NotFound();
        }

        // ETag 由时间戳 + 长度构成：文件被替换后浏览器自动失效
        var etag = $"\"{stamp.Ticks:x}-{new FileInfo(fullPath).Length:x}\"";
        var contentType = ResolveContentType(fullPath);

        return Results.File(fullPath, contentType, enableRangeProcessing: true, entityTag: null, lastModified: null)
            is var file
            ? WithCacheHeaders(file, etag)
            : file;
    }

    /// <summary>解析该会话的资源根目录；未知 scope 返回 null。</summary>
    private static string? ResolveRoot(
        string scope,
        string sessionId,
        IToolOutputStore outputStore,
        IWorkspaceProvider project)
    {
        if (string.Equals(scope, ScopeRef, StringComparison.OrdinalIgnoreCase))
            return outputStore.GetRefDirectory(sessionId);

        if (string.Equals(scope, ScopeUploads, StringComparison.OrdinalIgnoreCase))
        {
            var root = project.GetProjectRoot();
            return string.IsNullOrWhiteSpace(root)
                ? null
                : PastedFileLayout.BuildSessionDirectory(root, sessionId);
        }

        return null;
    }

    private static string ResolveContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "application/octet-stream"
    };

    private static IResult WithCacheHeaders(IResult result, string etag)
        => new AssetCacheResult(result, etag);

    /// <summary>给物理文件结果补 <c>Cache-Control</c> 与 <c>ETag</c>（物理文件结果本身不带）。</summary>
    private sealed class AssetCacheResult : IResult
    {
        private readonly IResult _inner;
        private readonly string _etag;

        public AssetCacheResult(IResult inner, string etag)
        {
            _inner = inner;
            _etag = etag;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            var headers = httpContext.Response.Headers;
            headers.CacheControl = "private, max-age=300";

            var requestEtag = httpContext.Request.Headers.IfNoneMatch.ToString();
            if (string.Equals(requestEtag, _etag, StringComparison.Ordinal))
            {
                httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                return Task.CompletedTask;
            }

            headers.ETag = _etag;
            return _inner.ExecuteAsync(httpContext);
        }
    }
}
