namespace Seeing.Agent.Tui.Rendering;

public sealed record TuiRenderOptions(
    int ToolPreviewLines = 12,
    bool ShowReasoning = false,
    int MaxInputLines = 6,
    int MaxCompletionRows = 8);
