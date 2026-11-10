using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.UiEnvelopeProbe;

/// <summary>
/// UI 贡献收集器：把「宿主贡献」与「模块贡献」两条路径分离。
/// <para>
/// <b>为什么分两段</b>：宿主壳层必须永远可用，不能绑到模块启停上（否则用户切换
/// Boot 能力集会失去 /settings 而无法自救）；模块贡献则必须随启停变化。
/// </para>
/// <para>
/// <b>三条入口（缺一即功能损坏）</b>：
/// ① 宿主段：<c>Build()</c> 之后、<c>InitializeSeeingAsync()</c> 之前（与工作区无关）；
/// ② 模块段正常路径：结算完成后、<c>ActivateAsync</c> 之前；
/// ③ 模块段 fallback 路径：无 <c>SettlementEngine</c> 时直接激活全部模块的分支。
/// </para>
/// </summary>
public sealed class UiContributionCollector
{
    private readonly IUiContributionRegistry _registry;
    private readonly IUiPointCatalog? _points;
    private readonly ILogger _logger;

    public UiContributionCollector(
        IUiContributionRegistry registry,
        IUiPointCatalog? points = null,
        ILogger? logger = null)
    {
        _registry = registry;
        _points = points;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>是否启用（无扩展点声明时整段 no-op）。</summary>
    public bool IsEnabled => _points is not null && _registry is UiContributionRegistry { IsEnabled: true };

    /// <summary>
    /// 收集宿主贡献（进程级，永不被模块启停门控）。
    /// <para>必须在 <c>InitializeSeeingAsync()</c> 之前调用，且与工作区是否选定无关。</para>
    /// </summary>
    /// <param name="hosts">宿主贡献者。</param>
    /// <returns>实际收集的宿主数（0 表示宿主没注册贡献，通常是接线遗漏）。</returns>
    public int CollectHostContributions(IEnumerable<IUiHostContributor> hosts)
    {
        if (!IsEnabled)
            return 0;

        var collected = 0;

        foreach (var host in hosts)
        {
            if (string.IsNullOrWhiteSpace(host.OwnerId))
            {
                _logger.LogError("宿主 UI 贡献的 OwnerId 为空，已跳过");
                continue;
            }

            if (!host.OwnerId.StartsWith("host.", StringComparison.Ordinal))
            {
                // 严重：若壳层 owner 不叫 host.*，其贡献会因「模块贡献必须有 Requires」
                // 被整批拒收 → 侧栏与设置页全空。必须在启动期报出来。
                _logger.LogError(
                    "宿主 UI 贡献的 OwnerId 必须以 'host.' 开头，实际为 '{OwnerId}'；" +
                    "否则其贡献会被当作模块贡献校验而整批拒收", host.OwnerId);
                continue;
            }

            var items = SafeDescribe(host.OwnerId, host.DescribeUi);
                if (items is null) continue;
                _registry.AddRange(host.OwnerId, items);
            collected++;
        }

        return collected;
    }

    /// <summary>
    /// 收集模块贡献。
    /// <para>
    /// 扫描<b>全部</b>模块（含未启用），因为「声明早于激活」：只有未启用模块的声明也在表里，
    /// 直链才能给出正确的「未启用」提示。
    /// </para>
    /// </summary>
    /// <param name="modules">全部模块（<c>IEnumerable&lt;ISeeingModule&gt;</c>）。</param>
    /// <returns>实际声明了贡献的模块数。</returns>
    public int CollectModuleContributions(IEnumerable<ISeeingModule> modules)
    {
        if (!IsEnabled)
            return 0;

        var collected = 0;

        foreach (var module in modules)
        {
            if (module is not IUiContributor contributor)
                continue;

            var items = SafeDescribe(module.Id, contributor.DescribeUi);
                if (items is null) continue;
                _registry.AddRange(module.Id, items);
            collected++;
        }

        return collected;
    }

    /// <summary>
    /// 声明求值：异常隔离 + null 过滤。
    /// <para>
    /// 绝不让单个模块的声明失败拖垮启动（这与「拒绝坏贡献」是两件事：
    /// 前者是声明本身抛错，后者是声明内容不合规）。
    /// </para>
    /// <para>
    /// 失败时<b>只记诊断</b>、不调用 <see cref="IUiContributionRegistry.AddRange"/>：
    /// <c>AddRange</c> 会清理该 owner 的旧诊断（幂等语义），会把刚写的诊断抹掉。
    /// </para>
    /// </summary>
    /// <returns>成功时返回贡献列表；失败时返回 null（调用方据此跳过 AddRange）。</returns>
    private IReadOnlyList<UiContribution>? SafeDescribe(
        string ownerId, Func<IReadOnlyList<UiContribution>> describe)
    {
        IReadOnlyList<UiContribution>? raw;
        try
        {
            raw = describe();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UI 贡献声明失败：{OwnerId}", ownerId);
            _registry.ReportInvalidDeclaration(ownerId, ex.Message);
            return null;
        }

        if (raw is null)
        {
            _registry.ReportInvalidDeclaration(ownerId, "DescribeUi 返回 null");
            return null;
        }

        return raw.Where(c => c is not null).ToArray();
    }
}

