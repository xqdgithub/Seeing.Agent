using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Output;
using Seeing.Agent.Paths;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// <see cref="IImagePartResolver"/> 的唯一实现：把 <c>LocalPath</c> 读成 base64。
/// <para>
/// <b>同步而非异步</b>：<c>SessionHistoryBuilder.BuildHistory</c> 是 static 同步纯函数，
/// 被十余处测试直接调用；改异步签名波及面远大于收益。LRU 命中为常数开销。
/// </para>
/// <para>
/// <b>安全边界</b>：路径必须落在**本会话**的 ref 目录内（<see cref="PathBoundary"/>）。
/// 否则会话文件里被写入任意路径就等于任意本地文件被 base64 送给模型。
/// </para>
/// <para>
/// <b>失败不抛</b>：图片丢失/越界一律降级为文本占位，一张历史图不该让整轮对话失败。
/// </para>
/// </summary>
public sealed class ImagePartResolver : IImagePartResolver
{
    /// <summary>默认缓存容量（项）。</summary>
    public const int DefaultCacheCapacity = 32;

    private readonly IServiceProvider _services;
    private readonly ILogger<ImagePartResolver> _logger;
    private readonly int _capacity;
    private readonly object _gate = new();

    // LRU：LinkedList 维护顺序（首 = 最旧），字典持有节点引用。键含路径 + 时间戳 + 长度。
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);

    /// <summary>创建解析器。</summary>
    /// <param name="services">
    /// 服务提供者。**不**直接注入 <see cref="IFileSystem"/>：它只在 <c>Seeing.IO.Local</c>
    /// 模块注册，硬依赖会让无该模块的宿主与 <c>ValidateOnBuild</c> 测试解析失败。
    /// </param>
    /// <param name="logger">日志。</param>
    /// <param name="capacity">缓存容量（项），≤0 时用 <see cref="DefaultCacheCapacity"/>。</param>
    public ImagePartResolver(
        IServiceProvider services,
        ILogger<ImagePartResolver>? logger = null,
        int capacity = DefaultCacheCapacity)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? NullLogger<ImagePartResolver>.Instance;
        _capacity = capacity > 0 ? capacity : DefaultCacheCapacity;
    }

    /// <inheritdoc />
    public ChatContentPart Resolve(string sessionId, SessionContentPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        return ResolveCore(
            sessionId,
            part.Type,
            part.LocalPath,
            part.DataBase64,
            part.Url,
            part.MimeType,
            part.FileName,
            part.ImageDetail);
    }

    /// <inheritdoc />
    public ChatContentPart Resolve(string sessionId, ToolOutputPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        return ResolveCore(
            sessionId,
            part.Kind,
            part.LocalPath,
            part.DataBase64,
            part.Url,
            part.MimeType,
            part.Name,
            imageDetail: null);
    }

    private ChatContentPart ResolveCore(
        string sessionId,
        string? kind,
        string? localPath,
        string? dataBase64,
        string? url,
        string? mimeType,
        string? name,
        string? imageDetail)
    {
        // 已经是可直接发送的载荷：原样透传
        if (!string.IsNullOrEmpty(url))
            return ChatContentPart.CreateImageFromUrl(url, imageDetail);

        if (!string.IsNullOrEmpty(dataBase64))
            return CreateFromBase64(kind, dataBase64, mimeType, name, imageDetail);

        if (string.IsNullOrEmpty(localPath))
            return ChatContentPart.CreateText(name is { Length: > 0 } ? $"[{name}]" : "[空内容块]");

        // 路径边界：必须是本会话 ref 目录内
        var refDir = ResolveRefDirectory(sessionId);
        if (refDir is null || !PathBoundary.IsWithin(localPath, refDir))
        {
            _logger.LogWarning(
                "拒绝物化越界路径（不在会话 ref 目录内）: Session={SessionId}, Path={Path}",
                sessionId, localPath);
            return ChatContentPart.CreateText($"[图片不可用：路径不在会话资源目录内]");
        }

        var base64 = ReadBase64(localPath);
        if (base64 is null)
            return ChatContentPart.CreateText($"[图片不可用：{Path.GetFileName(localPath)} 无法读取]");

        return CreateFromBase64(kind, base64, mimeType, name, imageDetail);
    }

    private static ChatContentPart CreateFromBase64(
        string? kind,
        string base64,
        string? mimeType,
        string? name,
        string? imageDetail)
    {
        var isFile = string.Equals(kind, ToolOutputPartKind.File, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(kind, ContentPartType.File, StringComparison.OrdinalIgnoreCase);

        var mime = string.IsNullOrEmpty(mimeType)
            ? (isFile ? "application/octet-stream" : "image/png")
            : mimeType;

        return isFile
            ? ChatContentPart.CreateFileFromBase64(base64, mime, name)
            : ChatContentPart.CreateImageFromBase64(base64, mime, imageDetail);
    }

    /// <summary>读盘并 base64；命中缓存时不读盘。失败返回 null（不抛）。</summary>
    private string? ReadBase64(string localPath)
    {
        var fileSystem = _services.GetService(typeof(IFileSystem)) as IFileSystem;

        long length;
        DateTime stamp;
        try
        {
            // 走 IFileSystem：遵守「禁止直接 File.*」纪律；缺失时返回 null。
            length = fileSystem?.GetFileLength(localPath) ?? new FileInfo(localPath).Length;
            stamp = fileSystem?.GetLastWriteTimeUtc(localPath) ?? File.GetLastWriteTimeUtc(localPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取图片元数据失败: {Path}", localPath);
            return null;
        }

        var key = $"{localPath}|{stamp.Ticks}|{length}";

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Base64;
            }
        }

        byte[] bytes;
        try
        {
            bytes = fileSystem is not null
                ? fileSystem.ReadAllBytes(localPath)
                : File.ReadAllBytes(localPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取图片失败（降级为文本占位）: {Path}", localPath);
            return null;
        }

        var base64 = Convert.ToBase64String(bytes);

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return existing.Value.Base64;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, base64));
            _order.AddFirst(node);
            _cache[key] = node;

            while (_cache.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }

        return base64;
    }

    /// <summary>解析该会话的 ref 目录；无法解析（未接入输出存储）返回 null。</summary>
    private string? ResolveRefDirectory(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (_services.GetService(typeof(IToolOutputStore)) is not IToolOutputStore store)
            return null;

        try
        {
            return store.GetRefDirectory(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "解析会话 ref 目录失败: {SessionId}", sessionId);
            return null;
        }
    }

    private readonly record struct CacheEntry(string Key, string Base64);
}
