using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Http;
using Seeing.Agent.Llm.Support.Speech;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Provider.MiMo;

/// <summary>
/// MiMo 语音转写客户端（<see cref="ISpeechTranscriptionClient"/>）。
/// <para><b>为什么不复用 OpenAI 客户端</b>：MiMo 的 ASR 不是转写端点，而是一个多模态 chat 模型——
/// 音频经 <c>messages[].content[].input_audio</c> 以 base64 data URL 送入
/// <c>POST /v1/chat/completions</c>，文本从 <c>choices[0].message.content</c> 取。
/// 走 OpenAI 的 multipart <c>/v1/audio/transcriptions</c> 会 404。</para>
/// <para><b>音频格式限制</b>：官方只支持 <c>mp3</c> 与 <c>wav</c>。其他格式在发请求前就拒绝，
/// 不静默改扩展名——那只会把可读错误变成服务端 400。</para>
/// <para>刻意没有能力自述属性（决议 15）。</para>
/// </summary>
public sealed class MiMoSpeechTranscriptionClient : ISpeechTranscriptionClient, IDisposable
{
    private const string ChatCompletionsPath = "chat/completions";

    /// <summary>官方支持的音频格式（MIME → <c>input_audio.format</c>）。</summary>
    private static readonly Dictionary<string, string> s_supportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = "mp3",
        ["audio/mp3"] = "mp3",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/wave"] = "wav",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public string ProviderId => _config.Id;

    /// <inheritdoc />
    public string ProviderType => _config.Type;

    /// <summary>
    /// 构造客户端。
    /// </summary>
    /// <param name="config">Provider 配置（取 Id / Type 用于诊断）。</param>
    /// <param name="httpClient">
    /// 已配置好 BaseAddress、代理与鉴权头的客户端（由 <c>LlmHttpClientFactory.Create</c> 产出，
    /// 代理语义与其它协议包一致）。<b>本类不释放它</b>，生命周期归 Provider 的客户端束。
    /// </param>
    /// <param name="logger">日志。</param>
    public MiMoSpeechTranscriptionClient(
        ProviderConfig config,
        HttpClient httpClient,
        ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    /// <remarks>HttpClient 由工厂创建并归 Provider 客户端束所有，此处不释放。</remarks>
    public void Dispose()
    {
        // 无自持资源：HttpClient 的所有权在外部
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = ResolveFormat(request);
        _logger.LogDebug("MiMo 语音转写（非流式）: Model={Model}, Format={Format}", request.Model, format);

        var body = await BuildRequestBodyAsync(request, format, stream: false, ct).ConfigureAwait(false);
        var json = await PostJsonAsync(body, call, ct).ConfigureAwait(false);

        var text = ExtractText(json);
        var usage = ExtractUsage(json);

        // MiMo 不返回段落级时间区间，segments 恒为 null（契约允许）
        return new TranscriptionResult(text, null, usage);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscriptSegment> TranscribeStreamAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = ResolveFormat(request);
        _logger.LogDebug("MiMo 语音转写（流式）: Model={Model}, Format={Format}", request.Model, format);

        var body = await BuildRequestBodyAsync(request, format, stream: true, ct).ConfigureAwait(false);

        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        ApplyOutbound(message, call);

        using var response = await _httpClient
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                yield break;

            var delta = TryExtractDelta(data);
            if (!string.IsNullOrEmpty(delta))
            {
                // 流式 delta 不携带时间区间；正文按序拼接即可
                yield return new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, delta!);
            }
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
                "MiMoSpeechTranscriptionClient 只服务 SpeechTranscription 能力，收到 {Capability} 连接测试请求",
                capability);
            return false;
        }

        try
        {
            // 决议 25：真实极短音频探针。MiMo 只收 mp3/wav，静音 WAV 正好合规。
            // 音频流归本方法所有，必须显式释放（决议 17 只约束实现不得释放调用方的流）。
            using var audio = new MemoryStream(SilentWavProbe.CreateBytes());
            var probe = new TranscriptionRequest
            {
                Model = modelId,
                Audio = audio,
                MimeType = SilentWavProbe.MimeType,
                FileName = SilentWavProbe.FileName,
            };

            var result = await TranscribeAsync(probe, call, ct).ConfigureAwait(false);
            return result.Text is not null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MiMo 语音转写连接测试失败");
            return false;
        }
    }

    #region 内部辅助

    /// <summary>校验并映射音频格式；不支持时抛可读异常（不静默改后缀）。</summary>
    private static string ResolveFormat(TranscriptionRequest request)
    {
        var mime = request.MimeType?.Trim();
        if (!string.IsNullOrEmpty(mime) && s_supportedFormats.TryGetValue(mime, out var mapped))
            return mapped;

        // 未给 MIME 时按文件名扩展名兜底
        var extension = Path.GetExtension(request.FileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (extension is "mp3" or "wav")
            return extension;

        throw new NotSupportedException(
            $"MiMo 语音转写仅支持 mp3 / wav 音频，收到 '{mime ?? "(未指定)"}'"
            + (string.IsNullOrEmpty(request.FileName) ? "" : $"（文件名 {request.FileName}）")
            + "。请在录音侧改用受支持格式。");
    }

    private async Task<string> BuildRequestBodyAsync(
        TranscriptionRequest request,
        string format,
        bool stream,
        CancellationToken ct)
    {
        // 音频整体读进内存再 base64：协议要求 data URL，无法流式上传。
        // 注意：不 Dispose request.Audio（决议 17，所有权在调用方）。
        var buffer = await ReadAllAsync(request.Audio, ct).ConfigureAwait(false);
        var dataUrl = $"data:{ToMime(format)};base64,{Convert.ToBase64String(buffer)}";

        var payload = new MiMoTranscriptionRequest
        {
            Model = request.Model,
            Stream = stream ? true : null,
            AsrOptions = new MiMoAsrOptions { Language = NormalizeLanguage(request.Language) },
            Messages =
            [
                new MiMoChatMessage
                {
                    Role = "user",
                    Content =
                    [
                        new MiMoContentPart
                        {
                            Type = "input_audio",
                            InputAudio = new MiMoInputAudio { Data = dataUrl }
                        }
                    ]
                }
            ]
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private async Task<string> PostJsonAsync(string body, LlmCallContext? call, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        ApplyOutbound(message, call);

        using var response = await _httpClient.SendAsync(message, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>非 2xx 时抛出带响应体的可读异常（响应体常含具体原因）。</summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogError(
            "MiMo 语音转写失败: Status={Status}, Body={Body}", (int)response.StatusCode, body);

        throw new InvalidOperationException(
            $"MiMo 语音转写失败（HTTP {(int)response.StatusCode}）：{Truncate(body, 500)}");
    }

    /// <summary>从 <c>choices[0].message.content</c> 取文本；结构异常时抛可读异常而非返回空串。</summary>
    private static string ExtractText(string json)
    {
        var api = Deserialize(json)
            ?? throw new InvalidOperationException($"MiMo 语音转写响应为空或无法解析：{Truncate(json, 300)}");

        var content = api.Choices?.FirstOrDefault()?.Message?.Content;
        if (content is null)
        {
            throw new InvalidOperationException(
                $"MiMo 语音转写响应缺少 choices[0].message.content：{Truncate(json, 300)}");
        }

        return content;
    }

    private static TokenUsage? ExtractUsage(string json)
    {
        var usage = Deserialize(json)?.Usage;
        if (usage is null)
            return null;

        return new TokenUsage
        {
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.CompletionTokens,
        };
    }

    /// <summary>解析流式增量；解析失败记警告后跳过（不中断整段转写）。</summary>
    private string? TryExtractDelta(string data)
    {
        try
        {
            var chunk = JsonSerializer.Deserialize<MiMoStreamChunk>(data, JsonOptions);
            return chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MiMo 转写 SSE 增量解析失败，原始数据: {Raw}", data);
            return null;
        }
    }

    private static MiMoTranscriptionResponse? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MiMoTranscriptionResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>语种：只透传 zh/en，其余一律 auto（官方仅支持三者，未知值不报错）。</summary>
    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "auto";

        var normalized = language.Trim().ToLowerInvariant();
        return normalized is "zh" or "en" ? normalized : "auto";
    }

    private static string ToMime(string format) => format == "mp3" ? "audio/mpeg" : "audio/wav";

    /// <summary>读尽整个流（不 Dispose 源流）。</summary>
    private static async Task<byte[]> ReadAllAsync(Stream source, CancellationToken ct)
    {
        if (source.CanSeek)
            source.Seek(0, SeekOrigin.Begin);

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private void ApplyOutbound(HttpRequestMessage message, LlmCallContext? call)
    {
        // 静态头（Authorization / headers 配置）在 HttpClient.DefaultRequestHeaders；
        // 此处只补 Call.ExtraHeaders，与既有 OpenAI 客户端的出站约定保持一致。
        if (call?.ExtraHeaders is not { Count: > 0 } headers)
            return;

        foreach (var (name, value) in headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
                message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    #endregion

    #region 线上模型

    private sealed class MiMoTranscriptionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<MiMoChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("asr_options")]
        public MiMoAsrOptions? AsrOptions { get; set; }

        [JsonPropertyName("stream")]
        public bool? Stream { get; set; }
    }

    private sealed class MiMoChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public List<MiMoContentPart> Content { get; set; } = [];
    }

    private sealed class MiMoContentPart
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "input_audio";

        [JsonPropertyName("input_audio")]
        public MiMoInputAudio? InputAudio { get; set; }
    }

    private sealed class MiMoInputAudio
    {
        /// <summary>data URL（<c>data:audio/wav;base64,...</c>）；format 由 MIME 派生，无需重复传。</summary>
        [JsonPropertyName("data")]
        public string Data { get; set; } = "";
    }

    private sealed class MiMoAsrOptions
    {
        [JsonPropertyName("language")]
        public string Language { get; set; } = "auto";
    }

    private sealed class MiMoTranscriptionResponse
    {
        [JsonPropertyName("choices")]
        public List<MiMoChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public MiMoUsage? Usage { get; set; }
    }

    private sealed class MiMoChoice
    {
        [JsonPropertyName("message")]
        public MiMoChoiceMessage? Message { get; set; }
    }

    private sealed class MiMoChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class MiMoUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }
    }

    private sealed class MiMoStreamChunk
    {
        [JsonPropertyName("choices")]
        public List<MiMoStreamChoice>? Choices { get; set; }
    }

    private sealed class MiMoStreamChoice
    {
        [JsonPropertyName("delta")]
        public MiMoStreamDelta? Delta { get; set; }
    }

    private sealed class MiMoStreamDelta
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    #endregion
}
