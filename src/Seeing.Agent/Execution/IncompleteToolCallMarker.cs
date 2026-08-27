using Seeing.Session.Core;

namespace Seeing.Agent.Execution;

/// <summary>
/// 将未完成的工具调用（pending/running）标记为已取消（服务端落盘用）。
/// <para>
/// 兜底：任何因取消、异常等原因未能走到终态的工具调用，
/// 在落盘前统一置为 cancelled，避免出现孤儿的运行中状态。
/// </para>
/// </summary>
internal static class IncompleteToolCallMarker
{
    public static int MarkCancelled(SessionData? session, string reason)
    {
        if (session?.Messages == null)
            return 0;

        var count = 0;
        foreach (var msg in session.Messages)
        {
            if (msg.ToolCalls == null)
                continue;

            foreach (var tc in msg.ToolCalls)
            {
                if (!IsIncomplete(tc))
                    continue;

                tc.Status = "cancelled";
                tc.Error = reason;
                count++;
            }
        }

        return count;
    }

    private static bool IsIncomplete(SessionToolCall tc)
    {
        return tc.Status is "pending" or "running"
            || string.Equals(tc.Status, "Pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tc.Status, "Running", StringComparison.OrdinalIgnoreCase);
    }
}
