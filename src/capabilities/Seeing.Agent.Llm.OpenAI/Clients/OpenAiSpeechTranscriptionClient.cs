using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Speech;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Llm.OpenAI.Clients;

/// <summary>
/// OpenAI Speech-to-Text 客户端（<see cref="ISpeechTranscriptionClient"/>，spec §7.10）。
/// <para><c>POST {baseUrl}/audio/transcriptions</c>（multipart）；非流式用 <c>verbose_json</c>
/// 一次取回全文与段落；流式用 <c>stream=true</c> 按 SSE delta 增量产出 <see cref="TranscriptSegment"/>。</para>
/// <para><b>流所有权（决议 17）</b>：<see cref="TranscriptionRequest.Audio"/> 由调用方拥有，
/// 本客户端只读、<b>绝不 Dispose</b>——上传链路上的 <see cref="StreamContent"/> 释放会级联到其包装流，
/// 因此上传前用 <see cref="NonDisposingStreamWrapper"/> 切断释放转发。</para>
/// <para>刻意没有能力自述属性（决议 15）。</para>
/// </summary>
public sealed class OpenAiSpeechTranscriptionClient : ISpeechTranscriptionClient, IDisposable
{
    private const string TranscriptionsPath = "audio/transcriptions";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;
    private readonly ILlmCallInterceptorRegistry? _interceptorRegistry;
    private readonly bool _ownsHttpClient;

    /// <inheritdoc />
    public string ProviderId => _config.Id;

    /// <inheritdoc />
    public string ProviderType => _config.Type;

    public OpenAiSpeechTranscriptionClient(
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
    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogDebug("SpeechTranscription 非流式: Model={Model}", request.Model);

        using var message = new HttpRequestMessage(HttpMethod.Post, TranscriptionsPath)
        {
            Content = BuildMultipartContent(request, stream: false)
        };
        ApplyOutbound(message, call);

        using var response = await _httpClient.SendAsync(message, ct);
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var api = OpenAiHttpHelper.Deserialize<TranscriptionApiResponse>(json, _logger)
            ?? throw new InvalidOperationException("转写响应为空或无法解析");

        // verbose_json 才有 segments；服务端未返回细粒度时为 null（契约语义）
        IReadOnlyList<TranscriptSegment>? segments = null;
        if (api.Segments is { Count: > 0 })
        {
            segments = api.Segments
                .Select(s => new TranscriptSegment(
                    TimeSpan.FromSeconds(s.Start),
                    TimeSpan.FromSeconds(s.End),
                    s.Text ?? ""))
                .ToList();
        }

        TokenUsage? usage = api.Usage is null
            ? null
            : new TokenUsage
            {
                InputTokens = api.Usage.InputTokens,
                OutputTokens = api.Usage.OutputTokens
            };

        return new TranscriptionResult(api.Text ?? "", segments, usage);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscriptSegment> TranscribeStreamAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogDebug("SpeechTranscription 流式: Model={Model}", request.Model);

        using var message = new HttpRequestMessage(HttpMethod.Post, TranscriptionsPath)
        {
            Content = BuildMultipartContent(request, stream: true)
        };
        ApplyOutbound(message, call);

        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                yield break;

            TranscriptionStreamDelta? delta;
            try
            {
                delta = OpenAiHttpHelper.Deserialize<TranscriptionStreamDelta>(data, _logger);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "转写 SSE 增量 JSON 解析失败，原始数据: {Raw}", data);
                continue;
            }

            if (delta is null || string.IsNullOrEmpty(delta.Delta))
                continue;

            // 流式 delta 协议不携带时间区间；Start/End 置零，正文按序拼接即可
            yield return new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, delta.Delta);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        if (!capability.HasFlag(LlmCapabilityKind.SpeechTranscription))
        {
            _logger.LogWarning(
                "OpenAiSpeechTranscriptionClient 只服务 SpeechTranscription 能力，收到 {Capability} 连接测试请求", capability);
            return false;
        }

        try
        {
            // 决议 25：传极短静音音频真实探针，校验响应结构合法（能解析即模型可用；空转写文本也算通过）。
            using var probe = new MemoryStream(SilentWavProbe.CreateBytes());
            var result = await TranscribeAsync(
                new TranscriptionRequest
                {
                    Model = modelId,
                    Audio = probe,
                    MimeType = SilentWavProbe.MimeType,
                    FileName = SilentWavProbe.FileName
                },
                call, ct);
            return result.Text is not null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeechTranscription 连接测试失败");
            return false;
        }
    }

    #region 内部辅助

    /// <summary>
    /// 构造 multipart 上传体。音频流经 <see cref="NonDisposingStreamWrapper"/> 包装：
    /// 请求/响应释放链会级联 Dispose 其内容流，而 <c>Audio</c> 归调用方所有（决议 17），不得被带走。
    /// </summary>
    private MultipartFormDataContent BuildMultipartContent(TranscriptionRequest request, bool stream)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(request.Model), "model");

        if (!string.IsNullOrWhiteSpace(request.Language))
            content.Add(new StringContent(request.Language), "language");

        if (stream)
        {
            // SSE 增量：协议只支持 json delta；段落时间区间由非流式 verbose_json 提供
            content.Add(new StringContent("true"), "stream");
        }
        else
        {
            content.Add(new StringContent("verbose_json"), "response_format");
        }

        var audioContent = new StreamContent(new NonDisposingStreamWrapper(request.Audio));
        if (!string.IsNullOrWhiteSpace(request.MimeType))
            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MimeType);
        content.Add(audioContent, "file", ResolveFileName(request));

        return content;
    }

    /// <summary>文件名：显式指定优先；否则按 MIME 推断扩展名（whisper 依据扩展名识别音频格式）。</summary>
    private static string ResolveFileName(TranscriptionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FileName))
            return request.FileName!;

        var ext = (request.MimeType ?? "").ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" or "audio/vnd.wave" => "wav",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/ogg" or "audio/opus" => "ogg",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "audio/mp4" or "audio/m4a" => "m4a",
            "audio/webm" => "webm",
            "audio/x-ms-wma" => "wma",
            _ => "mp3", // 未知 MIME 兜底按 mp3 上传（最常见音频形态）
        };
        return "audio." + ext;
    }

    private void ApplyOutbound(HttpRequestMessage message, LlmCallContext? call)
    {
        // 静态头（Authorization / headers 配置）已在 HttpClient.DefaultRequestHeaders；
        // 此处合并 Call.ExtraHeaders 与拦截器（与 PostJsonAsync 同一条出站管道）
        OutboundPipeline.Apply(
            message,
            staticHeaders: null,
            call,
            ResolveInterceptors(),
            _config.Id,
            _config.Type);
    }

    private IReadOnlyList<ILlmCallInterceptor> ResolveInterceptors()
        => _interceptorRegistry?.Resolve(ProviderId, ProviderType)
           ?? Array.Empty<ILlmCallInterceptor>();

    private static HttpClient ConfigureFactoryClient(HttpClient httpClient, ProviderConfig config, ILogger logger)
    {
        OpenAiHttpHelper.ConfigureHttpClient(httpClient, config, logger);
        return httpClient;
    }

    #endregion

    /// <summary>
    /// 只读流代理：<b>不转发 Dispose / DisposeAsync</b>（决议 17）。
    /// <para>HttpClient 释放请求内容时会级联释放 <see cref="StreamContent"/> 包装的流；
    /// <see cref="TranscriptionRequest.Audio"/> 归调用方所有，必须靠本包装切断该释放链。</para>
    /// </summary>
    private sealed class NonDisposingStreamWrapper : Stream
    {
        private readonly Stream _inner;

        public NonDisposingStreamWrapper(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException("音频上传流只读");

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("音频上传流只读");

        /// <summary>刻意空实现：所有权在调用方，本包装无权释放内层流。</summary>
        protected override void Dispose(bool disposing)
        {
            // 不调用 _inner.Dispose()，也不调用 base.Dispose(disposing)
        }

        /// <summary>同理切断异步释放路径（默认实现会转发到 DisposeAsync→Dispose）。</summary>
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
