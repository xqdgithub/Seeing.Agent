using Seeing.Session.Core;

namespace Seeing.Agent.App.Internal;

/// <summary>
/// 将未完成的 Task 工具调用标记为已取消（服务端落盘用）。
/// </summary>
internal static class IncompleteTaskMarker
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
                if (!IsIncompleteTask(tc))
                    continue;

                tc.Status = "cancelled";
                tc.Error = reason;
                count++;
            }
        }

        return count;
    }

    private static bool IsIncompleteTask(SessionToolCall tc)
    {
        var isTask = string.Equals(tc.Name, "task", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(tc.TaskId);
        if (!isTask)
            return false;

        return tc.Status is "pending" or "running"
            || string.Equals(tc.Status, "Pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tc.Status, "Running", StringComparison.OrdinalIgnoreCase);
    }
}
