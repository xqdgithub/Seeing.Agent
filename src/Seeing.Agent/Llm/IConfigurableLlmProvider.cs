using Seeing.Agent.Configuration;
using Seeing.ConfigSchema;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Llm;

/// <summary>
/// 支持通过配置 Schema 加载/保存连接字段的 LLM Provider。
/// </summary>
public interface IConfigurableLlmProvider
{
    IReadOnlyList<ConfigFieldSchema>? GetConfigSchema();

    Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(
        CancellationToken cancellationToken = default);

    Task SaveConfigAsync(
        IReadOnlyDictionary<string, object?> values,
        ConfigLevel level,
        CancellationToken cancellationToken = default);
}
