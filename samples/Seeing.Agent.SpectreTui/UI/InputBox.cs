using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.SpectreTui.Core;
using Seeing.Agent.SpectreTui.Core.State;

namespace Seeing.Agent.SpectreTui.UI;

/// <summary>
/// 输入框组件 - 显示用户输入区域和状态信息
/// 只负责显示，不处理键盘输入（由 InputService 处理）
/// Oracle 建议：不手动设置 Panel.Width，让 Layout 自动管理宽度分配
/// </summary>
public class InputBox
{
    private readonly InputState _inputState;
    private readonly AgentContext _agentContext;

    /// <summary>
    /// 创建 InputBox 实例
    /// </summary>
    public InputBox(InputState inputState, AgentContext agentContext)
    {
        _inputState = inputState ?? throw new ArgumentNullException(nameof(inputState));
        _agentContext = agentContext ?? throw new ArgumentNullException(nameof(agentContext));
    }

    /// <summary>
    /// 渲染输入面板（返回 Panel 对象供 Layout 使用）
    /// 不手动设置 Panel.Width，让 Layout 自动管理宽度分配
    /// </summary>
    public Panel RenderPanel()
    {
        // 构建输入面板内容
        var content = BuildInputContent();

        // 简化 Panel：去掉 Header、Border、Padding
        // Expand=true 让 Layout 自动管理宽度
        return new Panel(content)
        {
            Border = BoxBorder.None,
            Expand = true
        };
    }

    /// <summary>
    /// 构建输入内容（状态行 + 输入行）
    /// </summary>
    private IRenderable BuildInputContent()
    {
        // 状态信息行
        var statusRow = BuildStatusRow();

        // 输入内容行
        var inputRow = BuildInputRow();

        // 组合成垂直布局
        return new Rows(
            statusRow,
            new Text(""),  // 空行分隔
            inputRow
        );
    }

    /// <summary>
    /// 构建状态信息行（模式指示器 + 快捷键提示）
    /// </summary>
    private IRenderable BuildStatusRow()
    {
        // 模式指示器
        var modeText = _inputState.IsMultilineMode
            ? "[yellow]多行[/] Ctrl+Enter发送"
            : "[green]单行[/] Enter发送";

        // Agent 和模型信息
        var agentText = $"Agent: [cyan]{_agentContext.CurrentAgentKey}[/]";
        var modelText = $"Model: [blue]{_agentContext.CurrentModel ?? "default"}[/]";

        return new Markup($"[{ColorScheme.InfoColor}]{modeText} │ {agentText} │ {modelText}[/]");
    }

    /// <summary>
    /// 构建输入行（显示当前输入内容）
    /// </summary>
    private IRenderable BuildInputRow()
    {
        var input = _inputState.CurrentInput;

        // 如果输入为空，显示提示
        if (string.IsNullOrEmpty(input))
        {
            return new Markup("[dim]输入消息或命令...[/]");
        }

        // 转义输入内容中的 Markup 特殊字符
        var escapedInput = EscapeMarkup(input);

        // 显示输入内容
        return new Markup($"[green]> {escapedInput}[/]");
    }

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}