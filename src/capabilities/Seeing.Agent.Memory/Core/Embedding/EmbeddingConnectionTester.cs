using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// 向量连通性测试：经 <see cref="ILlmInvoker"/> 发一次单条 <see cref="IEmbeddingClient.EmbedAsync"/>
/// 真实探针（spec §7.2 决议 25），返回维度。
/// <para>旧的裸 HTTP 实现（自己拼 <c>{baseUrl}/embeddings</c>、依赖
/// <see cref="IEmbeddingConnectionTester"/> 之外的 <c>IProviderEndpointLookup</c>）已删除：
/// 那条路径绕过了重试、代理、Hook 与用量统计。</para>
/// </summary>
public sealed class EmbeddingConnectionTester : IEmbeddingConnectionTester
{
    /// <summary>探针文本：够短、能反映真实维度。</summary>
    private const string ProbeText = "seeing-memory-embedding-probe";

    private readonly ILlmInvoker _invoker;
    private readonly IModelConfigManager _models;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ILogger<EmbeddingConnectionTester>? _logger;

    public EmbeddingConnectionTester(
        ILlmInvoker invoker,
        IModelConfigManager models,
        IOptionsMonitor<MemoryOptions> options,
        ILogger<EmbeddingConnectionTester>? logger = null)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EmbeddingConnectionTestResult> TestAsync(
        string? modelId = null,
        CancellationToken ct = default)
    {
        // 只接受显式引用：入参为空则回落到配置的 ModelId，配置也为空即失败——绝不挑模型。
        var modelRef = string.IsNullOrWhiteSpace(modelId)
            ? _options.CurrentValue.Embedding.ModelId
            : modelId;

        if (string.IsNullOrWhiteSpace(modelRef))
            return new EmbeddingConnectionTestResult(false, "请先填写 Embedding ModelId（留空则不启用向量检索）");

        var wireModelId = EmbeddingModelReference.ToWireModelId(_models, modelRef);

        try
        {
            // 连接测试是「点一下按钮验证连通」的探针，不是业务调用：
            // 走 Invoker 以复用五层校验与重试，但 EmitHooks=false 避免把探针
            // 污染成 llm.params/after_complete 观测（spec §7.4.1）。
            var response = await _invoker.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                modelRef,
                (client, call, token) => client.EmbedAsync(
                    new EmbeddingRequest
                    {
                        Model = wireModelId,
                        Inputs = new[] { ProbeText },
                    },
                    call,
                    token),
                options: new LlmInvocationOptions { EmitHooks = false },
                cancellationToken: ct).ConfigureAwait(false);

            if (response.Dimensions <= 0)
                return new EmbeddingConnectionTestResult(false, "响应成功但未返回向量");

            return new EmbeddingConnectionTestResult(true, $"连接成功，维度={response.Dimensions}", response.Dimensions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 连接测试的失败即结果本体：转成 Success=false 供 UI 呈现，不是吞异常。
            _logger?.LogWarning(ex, "Embedding connection test failed for {ModelRef}", modelRef);
            return new EmbeddingConnectionTestResult(false, ex.Message);
        }
    }
}
