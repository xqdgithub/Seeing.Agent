using Microsoft.Extensions.Options;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// 向量检索可用性：<c>Enabled</c> + <c>ModelId</c> 非空 + 目录中存在该模型。
/// <para>三段判据依次为「用户是否同意开跑」「是否显式指定了模型」「指定的模型是否真的存在」。
/// 任一不满足都不启用向量索引，也<b>不做任何回落</b>：不读 <c>DefaultModels.embedding</c>、
/// 不从目录挑第一个 Embedding 模型（spec §0 决议 10 / §7.8.1 / §13 原则 1）。</para>
/// <para>目录依赖（<see cref="IModelManager"/>）是 Abstractions 接口，能力包引用合规；
/// 判定发生在调用/呈现期，<see cref="MemoryOptions.IsEmbeddingConfigured"/> 本身不查目录，
/// 以免 DI 期与异步填充的目录产生启动竞态。</para>
/// </summary>
public sealed class ConfigurableEmbeddingStatus : IEmbeddingStatus
{
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly IModelManager _models;

    public ConfigurableEmbeddingStatus(
        IOptionsMonitor<MemoryOptions> options,
        IModelManager models)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    /// <inheritdoc />
    public bool IsAvailable => ComputeReason() is null;

    /// <inheritdoc />
    public string? Reason => ComputeReason();

    private string? ComputeReason()
    {
        var embedding = _options.CurrentValue.Embedding;

        if (!embedding.Enabled)
            return "向量检索未启用（Embedding.Enabled=false）";

        if (string.IsNullOrWhiteSpace(embedding.ModelId))
            return "向量检索未启用：未配置 Embedding.ModelId（框架不会自动挑选模型）";

        if (_models.GetModel(embedding.ModelId) is null)
            return $"向量检索未启用：模型目录中不存在 '{embedding.ModelId}'";

        return null;
    }
}
