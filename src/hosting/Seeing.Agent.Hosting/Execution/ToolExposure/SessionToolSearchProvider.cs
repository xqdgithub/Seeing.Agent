using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// <see cref="IToolSearchProvider"/> 的 Hosting 实现：目录语料由 <see cref="ISessionToolSchemaResolver"/>
/// 单点结算的 deferred 条目提供，避免能力包自行重算会话结算口径导致分叉。
/// </summary>
public sealed class SessionToolSearchProvider : IToolSearchProvider
{
    private readonly ISessionToolSchemaResolver _resolver;

    public SessionToolSearchProvider(ISessionToolSchemaResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolCatalogEntry>> GetDiscoverableAsync(
        string sessionId,
        AgentDefinition? agent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || agent is null)
            return Array.Empty<ToolCatalogEntry>();

        var resolution = await _resolver.ResolveAsync(sessionId, agent, cancellationToken)
            .ConfigureAwait(false);
        // 检索语料为**全量 deferred**（不受显示预算裁剪影响）；显示目录另为 CatalogNamespaces。
        return resolution.DeferredEntries;
    }
}
