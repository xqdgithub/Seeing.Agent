namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 能力源注册表（仅模块 Activate/Deactivate 与 Manager 使用；禁止 UI/Provider 注入）。
/// </summary>
public interface IModelCapabilitySourceRegistry
{
    void Register(IModelCapabilitySource source);
    void Unregister(string sourceId);
    IReadOnlyList<IModelCapabilitySource> GetSources();

    event EventHandler? SourcesChanged;
}
