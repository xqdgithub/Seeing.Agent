using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// Provider 管理器接口 - 负责 Provider 配置和客户端生命周期。
/// <para>客户端按「Provider + 能力」取用（spec §7.3）：能力不匹配由 <see cref="ILlmRouter"/>
/// 在做五层校验时报错，本接口的出口只负责把 Provider 声明的客户端供出来。</para>
/// </summary>
public interface IProviderManager
{
    #region 查询

    /// <summary>获取所有已注册 Provider 的信息（含协议与能力投影）</summary>
    IReadOnlyDictionary<string, ProviderInfo> GetProviders();

    /// <summary>获取指定 Provider 的信息</summary>
    ProviderInfo? GetProvider(string providerId);

    /// <summary>若指定 Provider 支持可配置连接字段，则返回其实例。</summary>
    bool TryGetConfigurable(string providerId, out IConfigurableLlmProvider? configurable);

    #endregion

    #region 客户端管理

    /// <summary>获取指定 Provider 在指定能力上的客户端；未注册或未供出该能力返回 null（不抛）。</summary>
    ILlmCapabilityClient? GetClient(string providerId, LlmCapabilityKind capability);

    /// <summary>根据模型 ID 与能力解析对应的客户端</summary>
    /// <remarks>
    /// 解析逻辑：
    /// 1. 从 IModelConfigManager 获取模型配置
    /// 2. 根据 ModelConfig.Provider 取该能力的客户端
    /// <para>本出口不校验模型声明类型；需要类型 ↔ 能力校验时用 <see cref="ILlmRouter"/>。</para>
    /// </remarks>
    ILlmCapabilityClient? GetClientForModel(string modelId, LlmCapabilityKind capability);

    #endregion

    #region 连接测试

    /// <summary>测试 Provider 连接</summary>
    /// <param name="providerId">Provider ID</param>
    /// <param name="modelId">用于测试的模型 ID</param>
    /// <param name="capability">待测能力（决议 9）；缺省 Chat 仅为兼容既有调用点，UI 应显式传当前能力</param>
    /// <param name="ct">取消令牌</param>
    Task<bool> TestConnectionAsync(
        string providerId,
        string modelId,
        LlmCapabilityKind capability = LlmCapabilityKind.Chat,
        CancellationToken ct = default);

    #endregion

    #region 持久化

    /// <summary>保存 Provider 配置（仅用户级）</summary>
    Task SaveProviderAsync(
        string providerId,
        ProviderConfig config,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    /// <summary>删除 Provider 配置（仅用户级）</summary>
    Task DeleteProviderAsync(
        string providerId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    #endregion
}
