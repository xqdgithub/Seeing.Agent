using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 策略选择器：按模式纯映射到已注册策略（Auto 映射 Tiered，升级与否由输入快照的 Escalated 驱动）。
/// 不读取 options，无副作用。
/// </summary>
public sealed class ToolExposurePolicySelector
{
    private readonly Dictionary<ToolExposurePolicyMode, IToolExposurePolicy> _byMode;

    /// <summary>以已注册策略集合构建选择器。</summary>
    /// <param name="policies">策略集合（按各自 Mode 登记；同模式重复注册视为错误）。</param>
    public ToolExposurePolicySelector(IEnumerable<IToolExposurePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        _byMode = new Dictionary<ToolExposurePolicyMode, IToolExposurePolicy>();
        foreach (var policy in policies)
        {
            if (!_byMode.TryAdd(policy.Mode, policy))
            {
                throw new InvalidOperationException(
                    $"Duplicate IToolExposurePolicy registered for mode '{policy.Mode}'.");
            }
        }
    }

    /// <summary>按模式选择策略；Auto 与 Tiered 均由分层策略承担。</summary>
    /// <param name="mode">目标策略模式。</param>
    public IToolExposurePolicy Select(ToolExposurePolicyMode mode) => mode switch
    {
        ToolExposurePolicyMode.All => Require(ToolExposurePolicyMode.All),
        ToolExposurePolicyMode.Auto => Require(ToolExposurePolicyMode.Tiered),
        ToolExposurePolicyMode.Tiered => Require(ToolExposurePolicyMode.Tiered),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown tool exposure policy mode.")
    };

    private IToolExposurePolicy Require(ToolExposurePolicyMode mode) =>
        _byMode.TryGetValue(mode, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No IToolExposurePolicy registered for mode '{mode}'.");
}
