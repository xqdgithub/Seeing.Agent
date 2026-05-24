namespace Seeing.Agent.Core.Interfaces;

/// <summary>
/// 组件类型枚举
/// </summary>
public enum ComponentType
{
    /// <summary>技能</summary>
    Skill,
    /// <summary>MCP Server</summary>
    Mcp,
    /// <summary>插件/扩展</summary>
    Plugin,
    /// <summary>权限规则</summary>
    Rule
}

/// <summary>
/// 组件加载结果
/// </summary>
public class ComponentLoadResult
{
    /// <summary>组件类型</summary>
    public ComponentType Type { get; init; }

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>加载数量</summary>
    public int Count { get; init; }

    /// <summary>错误信息</summary>
    public string? Error { get; init; }

    /// <summary>详细信息</summary>
    public List<string> Details { get; init; } = new();
}

/// <summary>
/// 组件加载器接口 - 支持扩展自定义组件类型
/// </summary>
public interface IComponentLoader
{
    /// <summary>组件类型</summary>
    ComponentType Type { get; }

    /// <summary>加载组件</summary>
    Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 组件管理器接口 - 统一管理所有组件的发现和加载
/// </summary>
public interface IComponentManager
{
    /// <summary>注册自定义组件加载器</summary>
    void RegisterLoader(IComponentLoader loader);

    /// <summary>获取所有已注册的加载器</summary>
    IReadOnlyList<IComponentLoader> GetLoaders();

    /// <summary>加载所有组件</summary>
    Task<IReadOnlyList<ComponentLoadResult>> LoadAllAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    /// <summary>加载指定类型的组件</summary>
    Task<ComponentLoadResult> LoadAsync(
        ComponentType type,
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    /// <summary>获取组件加载状态</summary>
    IReadOnlyDictionary<ComponentType, ComponentLoadResult> GetLoadStatus();
}