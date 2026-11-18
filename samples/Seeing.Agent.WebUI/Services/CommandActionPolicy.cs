namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 命令动作的可用性判定（与具体组件解耦，便于单测）。
/// <para>
/// 子会话在命令执行时已由 <c>SessionRuntime.DeliverAsync</c> 隐式接管，故这里只判断
/// 宿主类型（ACP 透传）与载荷完整性。
/// </para>
/// </summary>
public static class CommandActionPolicy
{
    /// <summary>模型切换：ACP 透传不可用；载荷为空视为未处理。</summary>
    public static async Task<HostActionResult> ModelSetAsync(
        string? modelId,
        bool isAcpPassthrough,
        Func<string, Task>? setModel)
    {
        if (isAcpPassthrough)
            return new HostActionResult(
                HostActionStatus.Unavailable,
                "ACP 透传会话的模型由后端决定，无法通过命令切换。");

        if (string.IsNullOrWhiteSpace(modelId) || setModel is null)
            return new HostActionResult(HostActionStatus.NotHandled);

        await setModel(modelId).ConfigureAwait(false);
        return new HostActionResult(HostActionStatus.Handled, $"已切换模型：{modelId}");
    }

    /// <summary>Agent 切换：ACP 透传不可用；载荷为空视为未处理。</summary>
    public static async Task<HostActionResult> AgentSetAsync(
        string? agentId,
        bool isAcpPassthrough,
        Func<string, Task>? setAgent)
    {
        if (isAcpPassthrough)
            return new HostActionResult(
                HostActionStatus.Unavailable,
                "ACP 透传会话的 Agent 由后端决定，无法通过命令切换。");

        if (string.IsNullOrWhiteSpace(agentId) || setAgent is null)
            return new HostActionResult(HostActionStatus.NotHandled);

        await setAgent(agentId).ConfigureAwait(false);
        return new HostActionResult(HostActionStatus.Handled, $"已切换 Agent：{agentId}");
    }
}
