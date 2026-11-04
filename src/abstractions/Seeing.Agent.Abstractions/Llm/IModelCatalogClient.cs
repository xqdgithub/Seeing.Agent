namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 动态模型目录端口（可选）。
/// <para>不是能力端口：它不派生自 <see cref="ILlmCapabilityClient"/>，客户端自行选择是否实现，
/// 由 Provider 侧检测。当 <see cref="ProviderConfig.ListModels"/> 为 <c>true</c> 且客户端实现了本端口时，
/// 模型目录返回「静态 <c>config.Models</c> ∪ 远端目录」（spec §7.3）。</para>
/// <para>这样动态目录不需要专用 Provider 类，也不需要引用 Core。</para>
/// </summary>
public interface IModelCatalogClient
{
    /// <summary>列出远端可用模型。</summary>
    Task<IReadOnlyList<ModelConfig>> ListModelsAsync(CancellationToken cancellationToken = default);
}
