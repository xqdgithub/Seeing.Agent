using System.Text;

namespace Seeing.Agent.Mcp.Commands;

/// <summary>MCP Prompt 斜杠命令名：<c>mcp-prompt-{server}-{prompt}</c>，非法字符净化为 <c>_</c>。</summary>
internal static class McpPromptCommandNames
{
    public static string Build(string serverId, string promptName)
        => $"mcp-prompt-{Sanitize(serverId)}-{Sanitize(promptName)}";

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "_";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        return sb.ToString();
    }
}
