namespace Seeing.Agent.Tui.Rendering.Prompts;

/// <summary>模态列表项：主标签 + 可选说明。说明在富样式下以暗色副行展示（单物理行，超宽截断）。</summary>
/// <param name="Label">选项主文本（如「命令行 / Shell」）。</param>
/// <param name="Description">选项说明；为空则该项只占一行。</param>
public sealed record TuiListItem(string Label, string? Description = null);
