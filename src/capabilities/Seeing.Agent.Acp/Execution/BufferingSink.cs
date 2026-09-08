using System.Text;
using Acp.Types;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// 收集 ACP 文本输出（acpTool 同步模式）。
/// </summary>
public sealed class BufferingSink : IAcpUpdateSink
{
    private readonly StringBuilder _text = new();
    private readonly List<string> _toolSummaries = new();

    public Task OnSessionUpdateAsync(string acpSessionId, SessionUpdate update, CancellationToken cancellationToken = default)
    {
        switch (update)
        {
            case AgentMessageChunk chunk when chunk.Content is TextContentBlock text:
                _text.Append(text.Text);
                break;
            case ToolCallStart start:
                _toolSummaries.Add($"{start.ToolName}:{start.Status}");
                break;
            case ToolCallProgress progress when progress.Status is "completed" or "failed":
                _toolSummaries.Add($"{progress.ToolName}:{progress.Status}");
                break;
        }

        return Task.CompletedTask;
    }

    public string GetText() => _text.ToString().Trim();

    public IReadOnlyList<string> ToolSummaries => _toolSummaries;
}
