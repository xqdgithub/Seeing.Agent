namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 只读模型配置查询 — TokenBudget 等能力包用于读取 context limit，避免依赖完整 LLM 服务。
/// </summary>
public interface IModelConfigLookup
{
    /// <summary>获取指定模型配置；未知模型返回 null。</summary>
    ModelConfig? GetModelConfig(string modelId);
}
