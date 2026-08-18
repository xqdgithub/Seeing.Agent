using Seeing.Agent.Abstractions.Llm;
namespace Seeing.Agent.Llm;

/// <summary>
/// 提供 LLM 客户端与模型目录的 Provider 抽象。
/// </summary>
public interface ILlmProvider
{
    string Id { get; }

    string? Name { get; }

    ILlmClient GetClient();

    Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken = default);

    Task<bool> TestConnectionAsync(string modelId, CancellationToken cancellationToken = default);

    int MaxRetries { get; }
}
