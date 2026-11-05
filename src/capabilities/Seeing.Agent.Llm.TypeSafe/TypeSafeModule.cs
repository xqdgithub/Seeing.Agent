using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Llm.TypeSafe.Clients;

namespace Seeing.Agent.Llm.TypeSafe;

/// <summary>
/// TypeSafe（Decision）协议模块 —— id=<c>llm.typesafe</c>。
/// <para><b>本模块只管协议</b>：登记 typesafe 协议的客户端工厂，与 <c>llm.openai</c> 登记
/// <c>openai</c>/<c>openai-responses</c> 完全对等。它<b>不注册任何 Provider</b>——
/// 「某个具体端点怎么连、凭据从哪来」是插件（如 <c>provider.typesafe-api</c>）的职责，
/// 或由用户在 <c>Providers</c> 节自建配置驱动 Provider。</para>
/// <para>早先这里同时注册了一个 <c>ProviderOrigin.Builtin</c> 的 Provider，
/// 导致设置页把它当成「配置型」渲染、并给了它一份用户看不见的私有配置文件。那条路已删除。</para>
/// </summary>
public sealed class TypeSafeModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "llm.typesafe";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 协议→工厂唯一映射：typesafe 协议绑本工厂（重复登记在构造期即抛）。
        // 必须按具体类型解析——ILlmClientFactory 有多个实现，按接口取会拿到最后登记的那个。
        services.TryAddSingleton<TypeSafeClientFactory>();
        services.AddSingleton<ILlmClientFactory>(sp => sp.GetRequiredService<TypeSafeClientFactory>());
        services.AddSingleton<LlmClientFactoryRegistration>(sp => new(
            ProviderTypes.TypeSafe, sp.GetRequiredService<TypeSafeClientFactory>()));
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
