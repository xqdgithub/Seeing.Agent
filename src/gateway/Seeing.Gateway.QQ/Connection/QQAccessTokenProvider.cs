using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Gateway.QQ.Connection;

/// <summary>
/// QQ access_token 缓存
/// </summary>
public sealed class QQAccessTokenProvider
{
    private const string TokenUrl = "https://bots.qq.com/app/getAppAccessToken";
    private readonly QQOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QQAccessTokenProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public QQAccessTokenProvider(
        IOptions<QQOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<QQAccessTokenProvider> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_token) && DateTimeOffset.Now < _expiresAt - TimeSpan.FromMinutes(5))
            return _token!;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_token) && DateTimeOffset.Now < _expiresAt - TimeSpan.FromMinutes(5))
                return _token!;

            var client = _httpClientFactory.CreateClient(QQHttpApiClient.HttpClientName);
            using var response = await client.PostAsJsonAsync(
                TokenUrl,
                new { appId = _options.AppId, clientSecret = _options.ClientSecret },
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            var token = root.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("QQ token response missing access_token");
            var expiresIn = root.TryGetProperty("expires_in", out var exp)
                ? (exp.ValueKind == JsonValueKind.Number ? exp.GetInt32() : int.Parse(exp.GetString() ?? "7200"))
                : 7200;

            _token = token;
            _expiresAt = DateTimeOffset.Now.AddSeconds(expiresIn);
            _logger.LogDebug("QQ access_token refreshed, expires_in={ExpiresIn}s", expiresIn);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _token = null;
        _expiresAt = default;
    }
}
