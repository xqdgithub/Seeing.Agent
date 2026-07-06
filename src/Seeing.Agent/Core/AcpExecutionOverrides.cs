using Seeing.Agent.Core.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Core;

/// <summary>
/// ACP 透传执行时解析出的 model / mode 覆盖项。
/// </summary>
public sealed record AcpExecutionOverrides(string? ModelId, string? ModeId);

/// <summary>
/// 统一 Gateway / WebUI 的 ACP model、mode 解析与 AgentContext 写入。
/// </summary>
public static class AcpExecutionContextBuilder
{
    /// <summary>
    /// 解析 ACP 透传使用的 model / mode（不回退 Native DefaultModel）。
    /// </summary>
    public static AcpExecutionOverrides Resolve(
        AgentSelectionResolver resolver,
        string? requestModelId,
        string? requestModeId,
        SessionData session)
    {
        var modelId = resolver.ResolveAcpModelId(requestModelId, session.SelectedModel);
        var modeId = resolver.ResolveAcpModeId(requestModeId, session.SelectedAcpMode);
        return new AcpExecutionOverrides(modelId, modeId);
    }

    /// <summary>
    /// 将解析结果写入会话持久化字段。
    /// </summary>
    public static void ApplyToSession(SessionData session, AcpExecutionOverrides overrides)
    {
        if (!string.IsNullOrEmpty(overrides.ModelId))
            session.SelectedModel = overrides.ModelId;

        if (!string.IsNullOrEmpty(overrides.ModeId))
            session.SelectedAcpMode = overrides.ModeId;
    }

    /// <summary>
    /// 将解析结果写入 <see cref="AgentContext.Metadata"/>，供 ACP 执行器读取。
    /// </summary>
    public static void ApplyToContext(AgentContext context, AcpExecutionOverrides overrides)
    {
        if (!string.IsNullOrEmpty(overrides.ModeId))
            context.Metadata[AgentContextKeys.AcpModeId] = overrides.ModeId;

        if (!string.IsNullOrEmpty(overrides.ModelId))
            context.Metadata[AgentContextKeys.AcpModelId] = overrides.ModelId;
    }
}
