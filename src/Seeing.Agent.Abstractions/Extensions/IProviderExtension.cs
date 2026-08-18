using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 提供 LLM Provider 的扩展
/// </summary>
public interface IProviderExtension
{
    /// <summary>提供的 LLM Provider</summary>
    IEnumerable<ILlmProvider> GetProviders();
}