namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// Provider 的来源。**来源是注册时确定的既有事实，不是事后推断的**。
/// <para><b>为什么必须显式</b>：早先只有 <c>ownerExtensionId: string?</c> 一个字段，
/// 而来源实际有三种（配置驱动 / 扩展 / 内置），二者无法一一对应。
/// 于是每个使用方各自用「owner 是否为 null」猜来源，猜错的组合就会出错——
/// 典型症状：内置 Provider 的模型进不了模型目录、用户静态模型在「刷新当前」后消失。</para>
/// </summary>
public enum ProviderOrigin
{
    /// <summary>用户在 <c>Providers</c> 配置节里声明，由框架按配置构造。</summary>
    Configured,

    /// <summary>由扩展包（插件）注册。</summary>
    Extension,

    /// <summary>由框架内置能力包注册（当前无生产者：内置 Provider 已下沉为插件）。</summary>
    Builtin,
}

/// <summary>
/// 一次 Provider 注册的完整描述：实例 + 来源 + 归属 + 生命周期。
/// </summary>
/// <param name="Provider">Provider 实例。</param>
/// <param name="Origin">来源（决定目录聚合与 UI 呈现）。</param>
/// <param name="OwnerId">
/// 归属标识：<see cref="ProviderOrigin.Extension"/> 时为扩展 id；
/// <see cref="ProviderOrigin.Builtin"/> 时为模块 id；<see cref="ProviderOrigin.Configured"/> 时为 null。
/// </param>
/// <param name="DisposeByRegistry">
/// 注册表是否负责释放该实例。
/// <para><b>必须显式</b>：DI 单例归容器所有，若注册表在覆盖时把它释放掉，
/// 后续任何解析都会撞 <c>ObjectDisposedException</c>（实测：用户新增同 id 配置再删除，
/// 内置 Provider 永久不可用直到重启）。</para>
/// 由扩展/模块自己创建的实例归注册表释放；来自 DI 容器的实例不归注册表释放。
/// </param>
public sealed record ProviderRegistration(
    ILlmProvider Provider,
    ProviderOrigin Origin,
    string? OwnerId = null,
    bool DisposeByRegistry = true);
