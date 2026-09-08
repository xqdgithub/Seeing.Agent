using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Gateway.WeCom;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 下载并解密企微媒体，缓存到本地目录
/// </summary>
public sealed class WeComMediaFetcher
{
    public const string HttpClientName = "WeComMedia";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WeComOptions _options;
    private readonly ILogger<WeComMediaFetcher> _logger;

    public WeComMediaFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<WeComOptions> options,
        ILogger<WeComMediaFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WeComFetchedMedia?> FetchAsync(
        string url,
        string aesKey,
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            var encrypted = await httpClient.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            if (encrypted.Length > _options.MaxMediaBytes)
            {
                _logger.LogWarning("媒体文件过大: {Bytes} > {Max}", encrypted.Length, _options.MaxMediaBytes);
                return null;
            }

            var decrypted = WeComMediaDecryptor.Decrypt(encrypted, aesKey);
            if (decrypted.Length > _options.MaxMediaBytes)
                return null;

            var cacheDir = ResolveCacheDirectory();
            Directory.CreateDirectory(cacheDir);

            var safeName = SanitizeFileName(suggestedFileName);
            var filePath = Path.Combine(cacheDir, $"{Guid.NewGuid():N}_{safeName}");
            await File.WriteAllBytesAsync(filePath, decrypted, cancellationToken).ConfigureAwait(false);

            var mimeType = GuessMimeType(safeName, decrypted);
            return new WeComFetchedMedia
            {
                FilePath = filePath,
                MimeType = mimeType,
                SizeBytes = decrypted.Length
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载或解密企微媒体失败: {Url}", url);
            return null;
        }
    }

    public string ToDataUrl(WeComFetchedMedia media)
    {
        var bytes = File.ReadAllBytes(media.FilePath);
        var base64 = Convert.ToBase64String(bytes);
        return $"data:{media.MimeType};base64,{base64}";
    }

    private string ResolveCacheDirectory()
    {
        var configured = _options.MediaCacheDirectory;
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(Path.GetTempPath(), "seeing-wecom-media");

        return Environment.ExpandEnvironmentVariables(configured);
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "media.bin";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        return new string(chars);
    }

    private static string GuessMimeType(string fileName, byte[] data)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".pdf" => "application/pdf",
            ".amr" => "audio/amr",
            _ when data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 => "image/jpeg",
            _ when data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 => "image/png",
            _ => "application/octet-stream"
        };
    }
}

public sealed class WeComFetchedMedia
{
    public required string FilePath { get; init; }

    public required string MimeType { get; init; }

    public required int SizeBytes { get; init; }
}
