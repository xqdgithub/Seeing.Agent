using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Gateway.QQ;

public sealed class QQSessionTracker
{
    private readonly QQOptions _options;
    private readonly Dictionary<string, (string SessionId, DateTimeOffset LastActive)> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public QQSessionTracker(IOptions<QQOptions> options) => _options = options.Value;

    public string Resolve(ParsedQQMessage message)
    {
        var key = QQSessionResolver.ResolveSessionId(message, _options);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                if (_options.SessionIdleTimeoutMinutes > 0
                    && DateTimeOffset.Now - entry.LastActive > TimeSpan.FromMinutes(_options.SessionIdleTimeoutMinutes))
                {
                    entry = (QQSessionResolver.GenerateRotatedSessionId(key), DateTimeOffset.Now);
                    _map[key] = entry;
                    return entry.SessionId;
                }

                _map[key] = (entry.SessionId, DateTimeOffset.Now);
                return entry.SessionId;
            }

            _map[key] = (key, DateTimeOffset.Now);
            return key;
        }
    }

    public void Touch(ParsedQQMessage message)
    {
        var key = QQSessionResolver.ResolveSessionId(message, _options);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var entry))
                _map[key] = (entry.SessionId, DateTimeOffset.Now);
        }
    }

    public string Rotate(ParsedQQMessage message)
    {
        var key = QQSessionResolver.ResolveSessionId(message, _options);
        var rotated = QQSessionResolver.GenerateRotatedSessionId(key);
        lock (_lock)
        {
            _map[key] = (rotated, DateTimeOffset.Now);
        }
        return rotated;
    }
}

public sealed class QQCommandInterceptor
{
    public enum CommandKind { None, New, Clear }

    public CommandKind TryParse(string text, out string? stripped)
    {
        stripped = text?.Trim();
        if (string.IsNullOrEmpty(stripped) || stripped[0] != '/')
            return CommandKind.None;

        var cmd = stripped.Split([' ', '\t', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0]
            .ToLowerInvariant();
        return cmd switch
        {
            "/new" => CommandKind.New,
            "/clear" => CommandKind.Clear,
            _ => CommandKind.None
        };
    }
}

public sealed class QQMediaFetcher
{
    private static readonly Dictionary<string, string> s_extTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image", [".jpeg"] = "image", [".png"] = "image", [".gif"] = "image",
        [".webp"] = "image", [".bmp"] = "image",
        [".mp4"] = "video", [".avi"] = "video", [".mov"] = "video", [".mkv"] = "video",
        [".webm"] = "video", [".mpeg"] = "video",
        [".mp3"] = "audio", [".wav"] = "audio", [".ogg"] = "audio", [".m4a"] = "audio",
        [".aac"] = "audio", [".wma"] = "audio", [".amr"] = "audio", [".silk"] = "audio", [".slk"] = "audio"
    };

    private readonly QQOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QQMediaFetcher> _logger;

    public QQMediaFetcher(
        IOptions<QQOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<QQMediaFetcher> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Seeing.Gateway.Models.GatewayContentPart>> FetchAsync(
        IReadOnlyList<QQAttachmentMeta> attachments,
        CancellationToken cancellationToken)
    {
        var parts = new List<Seeing.Gateway.Models.GatewayContentPart>();
        if (attachments.Count == 0)
            return parts;

        var dir = _options.MediaCacheDirectory
            ?? Path.Combine(Path.GetTempPath(), "seeing-qq-media");
        Directory.CreateDirectory(dir);
        var client = _httpClientFactory.CreateClient(Connection.QQHttpApiClient.HttpClientName);

        foreach (var att in attachments)
        {
            try
            {
                if (att.IsVoice && !string.IsNullOrWhiteSpace(att.AsrText))
                {
                    parts.Add(new Seeing.Gateway.Models.GatewayTextContentPart(att.AsrText!));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(att.Url))
                    continue;

                using var response = await client.GetAsync(att.Url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length > _options.MaxMediaBytes)
                {
                    _logger.LogWarning("QQ media too large: {Bytes}", bytes.Length);
                    continue;
                }

                var mime = ResolveMime(att, response.Content.Headers.ContentType?.MediaType);
                var b64 = Convert.ToBase64String(bytes);
                var dataUrl = $"data:{mime};base64,{b64}";
                var kind = ResolveKind(att, mime);

                switch (kind)
                {
                    case "image":
                        parts.Add(new Seeing.Gateway.Models.GatewayImageContentPart(dataUrl, mime));
                        break;
                    case "audio":
                        parts.Add(new Seeing.Gateway.Models.GatewayAudioContentPart(dataUrl, mime));
                        break;
                    case "video":
                        parts.Add(new Seeing.Gateway.Models.GatewayFileContentPart(dataUrl, mime, att.FileName ?? "video"));
                        break;
                    default:
                        parts.Add(new Seeing.Gateway.Models.GatewayFileContentPart(dataUrl, mime, att.FileName));
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch QQ media {Url}", att.Url);
            }
        }

        return parts;
    }

    private static string ResolveMime(QQAttachmentMeta att, string? responseMime)
    {
        var ct = att.ContentType ?? responseMime ?? "application/octet-stream";
        if (ct.Equals("voice", StringComparison.OrdinalIgnoreCase))
            return "audio/amr";
        if (ct.Contains('/', StringComparison.Ordinal))
            return ct.Split(';')[0].Trim();
        return responseMime ?? "application/octet-stream";
    }

    private static string ResolveKind(QQAttachmentMeta att, string mime)
    {
        if (att.IsVoice)
            return "audio";
        var ct = att.ContentType ?? "";
        if (ct is "image" or "video" or "audio" or "file")
            return ct;
        var mimeLower = mime.Split(';')[0].Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "image/", "video/", "audio/" })
        {
            if (mimeLower.StartsWith(prefix, StringComparison.Ordinal))
                return prefix.TrimEnd('/');
        }

        var ext = Path.GetExtension(att.FileName ?? "");
        return s_extTypeMap.GetValueOrDefault(ext, "file");
    }
}
