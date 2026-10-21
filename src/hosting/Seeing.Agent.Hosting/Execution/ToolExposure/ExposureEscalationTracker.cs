namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 暴露升级判定：纯静态函数，阈值由调用方传入（不读取 options、无状态）。
/// 升级判定单调：一旦升级（alreadyEscalated）绝不自动降级。
/// </summary>
public static class ExposureEscalationTracker
{
    /// <summary>判定本轮是否应处于升级态（全内联）。</summary>
    /// <param name="toolCount">当前工具总数。</param>
    /// <param name="schemaTokens">当前 schema 估算 token 数。</param>
    /// <param name="alreadyEscalated">是否已处于升级态（为真则直接返回真，保持单调）。</param>
    /// <param name="escalateToolCount">工具数升级阈值。</param>
    /// <param name="escalateSchemaTokens">schema token 升级阈值。</param>
    /// <returns>升级态。</returns>
    public static bool ShouldEscalate(
        int toolCount,
        int schemaTokens,
        bool alreadyEscalated,
        int escalateToolCount,
        int escalateSchemaTokens)
    {
        if (alreadyEscalated)
        {
            return true;
        }

        return toolCount >= escalateToolCount || schemaTokens >= escalateSchemaTokens;
    }
}
