using System.Collections.Immutable;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 「协议 → 工厂」的解析端口：协议到工厂的<b>唯一映射</b>，不可变快照，线程安全读。
/// <para>替代原先的 <c>IEnumerable&lt;ILlmClientFactory&gt;</c> 扫描 + first-wins：
/// 从前同一协议登记两个工厂时后者静默失效，现在由注册端直接拒绝。</para>
/// <para>解析规则只有一条：<see cref="ProviderTypes.Normalize(string)"/> 后精确查表。
/// 不再有「遍历集合、逐个 <c>SupportsProtocol</c>、取第一个命中」的多重判定。</para>
/// </summary>
public interface ILlmClientFactoryResolver
{
    /// <summary>当前可用的全部协议（规范小写，稳定排序）。WebUI 协议下拉的取值来源。</summary>
    IReadOnlyList<string> Protocols { get; }

    /// <summary>按协议取工厂；未登记返回 <c>null</c>（不抛）。</summary>
    ILlmClientFactory? Find(string protocol);

    /// <summary>按协议取工厂；未登记抛 <see cref="NotSupportedException"/>（带可用协议清单）。</summary>
    ILlmClientFactory Require(string protocol);
}

/// <summary>
/// <see cref="ILlmClientFactoryResolver"/> 的不可变快照实现。
/// <para>在 <c>ConfigureServices</c> 期由各 LLM 模块登记，运行时只读。</para>
/// </summary>
public sealed class LlmClientFactoryResolver : ILlmClientFactoryResolver
{
    private readonly ImmutableDictionary<string, ILlmClientFactory> _factories;

    /// <summary>由注册记录构造（重复协议在构造期即抛）。</summary>
    public LlmClientFactoryResolver(IEnumerable<LlmClientFactoryRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var builder = ImmutableDictionary.CreateBuilder<string, ILlmClientFactory>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var registration in registrations)
        {
            var protocol = ProviderTypes.Normalize(registration.Protocol);
            if (string.IsNullOrWhiteSpace(protocol))
                continue;

            // 重复登记不静默覆盖：否则其中一个工厂永远不被用到，且无从发现。
            if (builder.TryGetValue(protocol, out var existing) &&
                !ReferenceEquals(existing, registration.Factory))
            {
                throw new InvalidOperationException(
                    $"协议 '{protocol}' 被重复登记：{existing.GetType().Name} 与 "
                    + $"{registration.Factory.GetType().Name}。同一协议只允许一个工厂。");
            }

            builder[protocol] = registration.Factory;
        }

        _factories = builder.ToImmutable();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Protocols =>
        _factories.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <inheritdoc />
    public ILlmClientFactory? Find(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return null;

        return _factories.TryGetValue(ProviderTypes.Normalize(protocol), out var factory)
            ? factory
            : null;
    }

    /// <inheritdoc />
    public ILlmClientFactory Require(string protocol)
    {
        var factory = Find(protocol);
        if (factory is not null)
            return factory;

        throw new NotSupportedException(
            $"不支持的 Provider 协议: '{protocol}'。当前已登记："
            + (Protocols.Count == 0 ? "（无）" : string.Join(", ", Protocols)));
    }
}
