using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Acp.Hosting;

/// <summary>
/// ACP 配置变更重载处理器：挂接孤儿接口 <see cref="IAcpConfigurationReloader"/>，
/// 当 Acp 配置节变更（或全量重载）时触发 Passthrough Agent 重新注册。
/// </summary>
public sealed class AcpReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly IAcpConfigurationReloader _reloader;

    public AcpReloadHandler(IAcpConfigurationReloader reloader) => _reloader = reloader;

    /// <inheritdoc/>
    public override string ComponentId => "acp";

    /// <inheritdoc/>
    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0 || change.ChangedSections.Contains("Acp"))
            return _reloader.ReloadAsync(ct);
        return Task.CompletedTask;
    }
}
