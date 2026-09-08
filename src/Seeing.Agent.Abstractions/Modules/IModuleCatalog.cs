namespace Seeing.Agent.Abstractions.Modules;

/// <summary>
/// 模块目录只读视图 — 宿主与 CLI 查询 available / enabled / unhealthy 状态
/// </summary>
public interface IModuleCatalog
{
    /// <summary>宿主引用的全部模块描述</summary>
    IReadOnlyCollection<ModuleDescriptor> Available { get; }

    /// <summary>当前启用的模块 id</summary>
    IReadOnlyCollection<string> Enabled { get; }

    /// <summary>不健康模块 id → 失败原因（无原因时为 null）</summary>
    IReadOnlyDictionary<string, string?> Unhealthy { get; }

    /// <summary>模块 id 是否在启用集中</summary>
    bool IsEnabled(string id);

    /// <summary>模块 id 是否在可用目录中</summary>
    bool IsAvailable(string id);
}
