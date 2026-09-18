namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>重载变更数据基接口：每种触发源一个强类型实现</summary>
public interface IReloadSignal { }

/// <summary>配置节变更数据</summary>
public sealed class ConfigChange : IReloadSignal
{
    /// <summary>发生变更的配置节名称列表（空数组表示全量重载）</summary>
    public IReadOnlyList<string> ChangedSections { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 变更节内的细粒度键（如 Providers 节下变更的 provider id）。
    /// <para>空表示未提供作用域，消费方应回退为按节全量处理。</para>
    /// </summary>
    public IReadOnlyList<string> ChangedKeys { get; init; } = Array.Empty<string>();
}

/// <summary>工作区变更数据</summary>
public sealed class WorkspaceChange : IReloadSignal
{
    /// <summary>旧工作区根目录</summary>
    public string? OldWorkspace { get; init; }

    /// <summary>新工作区根目录</summary>
    public string? NewWorkspace { get; init; }
}
