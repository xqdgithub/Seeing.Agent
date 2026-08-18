using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 扩展管理器契约 - 扩展的加载、激活与生命周期管理
/// </summary>
public interface IExtensionManager
{
    /// <summary>获取所有已加载的扩展</summary>
    IReadOnlyCollection<LoadedExtension> GetAll();

    /// <summary>获取指定扩展</summary>
    LoadedExtension? Get(string id);

    /// <summary>获取扩展状态列表</summary>
    IEnumerable<ExtensionStatus> ListStatus();

    /// <summary>加载并初始化扩展</summary>
    Task InitializeAsync(
        IEnumerable<PluginSpec> specs,
        Dictionary<string, bool>? enabledOverrides,
        ExtensionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>激活扩展（初始化并注册组件）</summary>
    Task<bool> ActivateAsync(string id, ExtensionContext context);

    /// <summary>停用扩展</summary>
    Task<bool> DeactivateAsync(string id);

    /// <summary>添加新扩展</summary>
    Task<bool> AddAsync(string spec, ExtensionContext context);

    /// <summary>释放全部扩展</summary>
    Task DisposeAllAsync();

    /// <summary>注册扩展释放回调</summary>
    void RegisterDisposeCallback(string extensionId, Func<Task> callback);
}