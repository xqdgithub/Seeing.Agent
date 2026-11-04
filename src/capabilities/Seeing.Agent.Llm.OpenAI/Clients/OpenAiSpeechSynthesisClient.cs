using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Llm.OpenAI.Clients;

/// <summary>
/// OpenAI Text-to-Speech 客户端（<see cref="ISpeechSynthesisClient"/>，spec §7.10）。
/// <para><c>POST {baseUrl}/audio/speech</c>；复用 <see cref="LlmHttpClientFactory"/> 与
/// <see cref="OutboundPipeline"/>（经 <see cref="OpenAiHttpHelper.PostJsonAsync"/>）。</para>
/// <para>非流式返回完整音频字节；流式按响应体<b>自然分块</b>产出 <see cref="AudioChunk"/>
/// ——读取缓冲只是单次读取上限，不做固定 8KB 之类的重新分帧。</para>
/// <para>刻意没有能力自述属性（决议 15）。</para>
/// </summary>
public sealed class OpenAiSpeechSynthesisClient : ISpeechSynthesisClient, IDisposable
{
    /// <summary>流式单次读取的缓冲上限（64KB）；实际分块大小由响应体自然供给决定。</summary>
    private const int StreamReadBufferBytes = 64 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;
    private readonly ILlmCallInterceptorRegistry? _interceptorRegistry;
    private readonly bool _ownsHttpClient;

    /// <inheritdoc />
    public string ProviderId => _config.Id;

    /// <inheritdoc />
    public string ProviderType => _config.Type;

    public OpenAiSpeechSynthesisClient(
        ProviderConfig config,
        HttpClient httpClient,
        ILogger logger,
        ILlmCallInterceptorRegistry? interceptorRegistry = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(httpClient);
        _interceptorRegistry = interceptorRegistry;

        // 与其他 OpenAI 客户端一致的所有权约定：共享 HttpClient 不释放，工厂新建的由本客户端配置并拥有
        var ownsClient = httpClient.BaseAddress == null;
        _httpClient = ownsClient
            ? ConfigureFactoryClient(httpClient, config, logger)
            : httpClient;
        _ownsHttpClient = ownsClient;
    }

    /// <summary>
    /// 释放自建 HttpClient（共享客户端不释放）。
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    /// <inheritdoc />
    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        var body = BuildRequest(request);
        _logger.LogDebug("SpeechSynthesis 非流式: Model={Model}, Voice={Voice}", body.Model, body.Voice);

        using var response = await OpenAiHttpHelper.PostJsonAsync(
            _httpClient, "audio/speech", body, _logger, ct, _config, call, ResolveInterceptors());
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var audio = await response.Content.ReadAsByteArrayAsync(ct);
        var mimeType = ResolveMimeType(response, body.ResponseFormat);
        // OpenAI TTS 端点不返回 token usage
        return new SpeechSynthesisResult(audio, mimeType, null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioChunk> SynthesizeStreamAsync(
        SpeechSynthesisRequest request,
        LlmCallContext? call = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequest(request);
        _logger.LogDebug("SpeechSynthesis 流式: Model={Model}, Voice={Voice}", body.Model, body.Voice);

        // PostJsonAsync 已使用 ResponseHeadersRead，响应体保持流式
        using var response = await OpenAiHttpHelper.PostJsonAsync(
            _httpClient, "audio/speech", body, _logger, ct, _config, call, ResolveInterceptors());
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var mimeType = ResolveMimeType(response, body.ResponseFormat);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        // 按响应体自然分块产出：每次读取返回多少就产出多少，不做固定帧长的重划分；
        // 拷贝出独立数组，避免下一轮读取覆盖已交付的分块。
        var buffer = new byte[StreamReadBufferBytes];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read <= 0)
                break;
            yield return new AudioChunk(buffer.AsMemory(0, read).ToArray(), mimeType);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        if (!capability.HasFlag(LlmCapabilityKind.SpeechSynthesis))
        {
            _logger.LogWarning(
                "OpenAiSpeechSynthesisClient 只服务 SpeechSynthesis 能力，收到 {Capability} 连接测试请求", capability);
            return false;
        }

        try
        {
            // 决议 25：合成一句短句真实探针，校验返回非空音频字节。
            var result = await SynthesizeAsync(
                new SpeechSynthesisRequest { Model = modelId, Text = "连接测试" },
                call, ct);
            var ok = result.Audio.Length > 0;
            if (!ok)
                _logger.LogWarning("SpeechSynthesis 连接测试返回空音频: Model={Model}", modelId);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeechSynthesis 连接测试失败");
            return false;
        }
    }

    #region 内部辅助

    private SpeechSynthesisApiRequest BuildRequest(SpeechSynthesisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new SpeechSynthesisApiRequest
        {
            Model = request.Model,
            Input = request.Text,
            // 未指定音色/格式时回落到全模型通用的默认值（alloy 为 OpenAI TTS 全系支持音色）
            Voice = string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice,
            ResponseFormat = string.IsNullOrWhiteSpace(request.Format) ? "mp3" : request.Format,
            Speed = request.Speed
        };
    }

    /// <summary>优先取响应 Content-Type；服务端给出 octet-stream 时按请求格式推断 MIME。</summary>
    private static string ResolveMimeType(HttpResponseMessage response, string? format)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return mediaType!;
        }

        return MimeTypeForFormat(format);
    }

    private static string MimeTypeForFormat(string? format) =>
        (format ?? "mp3").Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "pcm" => "audio/L16",
            _ => "application/octet-stream",
        };

    private IReadOnlyList<ILlmCallInterceptor> ResolveInterceptors()
        => _interceptorRegistry?.Resolve(ProviderId, ProviderType)
           ?? Array.Empty<ILlmCallInterceptor>();

    private static HttpClient ConfigureFactoryClient(HttpClient httpClient, ProviderConfig config, ILogger logger)
    {
        OpenAiHttpHelper.ConfigureHttpClient(httpClient, config, logger);
        return httpClient;
    }

    #endregion
}
