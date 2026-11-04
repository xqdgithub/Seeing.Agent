using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.OpenAI.Clients;

/// <summary>
/// OpenAI Embeddings API 客户端（<see cref="IEmbeddingClient"/>，spec §7.8）。
/// <para><c>POST {baseUrl}/embeddings</c>；复用 <see cref="LlmHttpClientFactory"/> 与
/// <see cref="OutboundPipeline"/>（经 <see cref="OpenAiHttpHelper.PostJsonAsync"/>）。</para>
/// <para>响应 <c>data[]</c> 的 <c>index</c> 可能乱序（服务端并行批处理），
/// 本客户端按 <see cref="EmbeddingVector.Index"/> 升序回排，保证与请求 Inputs 顺序对齐。</para>
/// <para>刻意没有能力自述属性（决议 15）：能力真相在 <see cref="ProviderConfig.Capabilities"/>。</para>
/// </summary>
public sealed class OpenAiEmbeddingClient : IEmbeddingClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;
    private readonly ILlmCallInterceptorRegistry? _interceptorRegistry;
    private readonly bool _ownsHttpClient;

    /// <inheritdoc />
    public string ProviderId => _config.Id;

    /// <inheritdoc />
    public string ProviderType => _config.Type;

    public OpenAiEmbeddingClient(
        ProviderConfig config,
        HttpClient httpClient,
        ILogger logger,
        ILlmCallInterceptorRegistry? interceptorRegistry = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(httpClient);
        _interceptorRegistry = interceptorRegistry;

        // 与 OpenAiChatClient 一致的所有权约定：
        // 已配置 BaseAddress 的共享 HttpClient 不被拥有；工厂新建的在此配置并由客户端拥有。
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
    public async Task<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 空输入不发请求，直接回空集（避免服务端 400）
        if (request.Inputs.Count == 0)
            return new EmbeddingResponse { Vectors = Array.Empty<EmbeddingVector>() };

        _logger.LogDebug("Embeddings 请求: Model={Model}, Inputs={Count}", request.Model, request.Inputs.Count);

        var body = new EmbeddingApiRequest
        {
            Model = request.Model,
            Input = request.Inputs.ToList(),
            Dimensions = request.Dimensions,
            EncodingFormat = request.EncodingFormat
        };

        using var response = await OpenAiHttpHelper.PostJsonAsync(
            _httpClient, "embeddings", body, _logger, ct, _config, call, ResolveInterceptors());
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var api = OpenAiHttpHelper.Deserialize<EmbeddingApiResponse>(json, _logger)
            ?? throw new InvalidOperationException("Embeddings 响应为空或无法解析");

        // data[] 的 index 可能乱序，按 Index 升序回排后再与原文对齐
        var vectors = new List<EmbeddingVector>();
        foreach (var item in (api.Data ?? new List<EmbeddingApiItem>()).OrderBy(d => d.Index))
        {
            var text = item.Index >= 0 && item.Index < request.Inputs.Count
                ? request.Inputs[item.Index]
                : string.Empty;
            var values = item.Embedding?.ToArray() ?? Array.Empty<float>();
            vectors.Add(new EmbeddingVector(text, values, item.Index));
        }

        return new EmbeddingResponse
        {
            Vectors = vectors,
            Usage = MapUsage(api.Usage)
        };
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        if (!capability.HasFlag(LlmCapabilityKind.Embedding))
        {
            _logger.LogWarning("OpenAiEmbeddingClient 只服务 Embedding 能力，收到 {Capability} 连接测试请求", capability);
            return false;
        }

        try
        {
            // 决议 25：单条短文本真实探针，校验返回维度 > 0。
            // 只探端点会把「模型不存在」误判为连通，真实请求是唯一能验证模型可用的方式。
            var response = await EmbedAsync(
                new EmbeddingRequest { Model = modelId, Inputs = new[] { "connection probe" } },
                call, ct);
            var ok = response.Dimensions > 0;
            if (!ok)
                _logger.LogWarning("Embedding 连接测试返回零维度向量: Model={Model}", modelId);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding 连接测试失败");
            return false;
        }
    }

    private IReadOnlyList<ILlmCallInterceptor> ResolveInterceptors()
        => _interceptorRegistry?.Resolve(ProviderId, ProviderType)
           ?? Array.Empty<ILlmCallInterceptor>();

    private static HttpClient ConfigureFactoryClient(HttpClient httpClient, ProviderConfig config, ILogger logger)
    {
        OpenAiHttpHelper.ConfigureHttpClient(httpClient, config, logger);
        return httpClient;
    }

    private static TokenUsage? MapUsage(OpenAiUsage? u)
    {
        if (u == null) return null;
        return new TokenUsage
        {
            InputTokens = u.PromptTokens,
            OutputTokens = 0
        };
    }
}
