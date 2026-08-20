namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型扩展元数据（<see cref="ModelConfig.Metadata"/>）的预定义键。
/// 供 Provider 插件写入、WebUI 等消费方读取，避免字符串魔数漂移。
/// </summary>
public static class ModelMetadataKeys
{
    /// <summary>是否免费模型（值为 bool）</summary>
    public const string IsFree = "isFree";
}
