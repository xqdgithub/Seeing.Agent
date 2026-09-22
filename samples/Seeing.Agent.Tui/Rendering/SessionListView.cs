using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 会话列表（纯渲染辅助）：按 UpdatedAt 倒序展示，活跃会话以 * 标记。
/// </summary>
public static class SessionListView
{
    public static IRenderable Render(IReadOnlyList<SessionData> sessions, int width, string? activeSessionId = null)
    {
        try
        {
            if (sessions is null || sessions.Count == 0)
                return new Text("(无会话)");

            var table = new Table();
            table.AddColumn("标题");
            table.AddColumn("Agent");
            table.AddColumn("消息");
            table.AddColumn("更新于");
            table.AddColumn("会话 Id");

            foreach (var session in sessions.OrderByDescending(s => s.UpdatedAt))
            {
                var title = string.Equals(session.Id, activeSessionId, StringComparison.Ordinal)
                    ? $"* {session.Title}"
                    : session.Title;

                table.AddRow(
                    new Text(title ?? string.Empty),
                    new Text(string.IsNullOrEmpty(session.SelectedAgent) ? "-" : session.SelectedAgent),
                    new Text(session.MessageCount.ToString()),
                    new Text(session.UpdatedAt.ToString("MM-dd HH:mm")),
                    new Text(session.Id));
            }

            return table;
        }
        catch
        {
            return new Text("(会话列表渲染失败)");
        }
    }
}
