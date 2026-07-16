using Seeing.Agent.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>
/// Provider 管理器接口 - 负责 Provider 配置和客户端生命周期
/// </summary>
public interface IProviderManager
{
    #region 查询

    /// <summary>获取所有 Provider 配置</summary>
    IReadOnlyDictionary<string, ProviderConfig> GetProviders();

    /// <summary>获取指定 Provider 配置</summary>
    ProviderConfig? GetProvider(string providerId);

    /// <summary>获取默认 Provider ID</summary>
    string? GetDefaultProvider();

    #endregion

    #region 客户端管理

    /// <summary>获取指定 Provider 的客户端</summary>
    ILlmClient? GetClient(string providerId);

    /// <summary>根据模型 ID 解析对应的客户端</summary>
    /// <remarks>
    /// 解析逻辑：
    /// 1. 从 IModelConfigManager 获取模型配置
    /// 2. 根据 ModelConfig.Provider 获取对应客户端
    /// </remarks>
    ILlmClient? GetClientForModel(string modelId);

    #endregion

    #region 连接测试

    /// <summary>测试 Provider 连接</summary>
    Task<bool> TestConnectionAsync(string providerId, CancellationToken ct = default);

    #endregion

    #region 持久化

    /// <summary>保存 Provider 配置</summary>
    Task SaveProviderAsync(
        string providerId,
        ProviderConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    /// <summary>删除 Provider 配置</summary>
    Task DeleteProviderAsync(
        string providerId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    /// <summary>设置默认 Provider</summary>
    Task SetDefaultProviderAsync(
        string? providerId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    #endregion
}
