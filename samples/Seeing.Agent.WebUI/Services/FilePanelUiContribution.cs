using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Components;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 文件面板插槽贡献（宿主层包装组件）。开关入口已并入工作目录徽标。
/// <para>
/// <b>为什么还在旧形状</b>：插槽通道（<c>SlotHost</c>）在本批次不动，
/// 它的迁移与设置卡一起放到批次 D。归属模块 <c>files.workspace</c> 保持不变。
/// </para>
/// </summary>
public sealed class FilePanelUiContribution : IUiContribution
{
    /// <inheritdoc />
    public string ModuleId => "files.workspace";

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new LegacySlotContribution("session_file_panel", typeof(SessionFilePanel), Requires: ["files.workspace"]),
    ];
}
