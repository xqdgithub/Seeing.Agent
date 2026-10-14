using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Components;

namespace Seeing.Agent.WebUI.Services;

/// <summary>文件面板 UI 贡献（宿主层包装组件）。开关入口已并入工作目录徽标。</summary>
public sealed class FilePanelUiContribution : IUiContribution
{
    /// <inheritdoc />
    public string ModuleId => "files.workspace";

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new SlotContribution("session_file_panel", typeof(SessionFilePanel), Requires: ["files.workspace"]),
    ];
}
