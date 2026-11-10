using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Components;
using Seeing.Agent.WebUI.Shell;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 文件面板插槽贡献（宿主层包装组件）。
/// <para>
/// <b>为什么在 WebUI 而不是 <c>Seeing.Agent.Files</c></b>：贡献的视图键最终要解析成
/// <see cref="SessionFilePanel"/> —— 那是 WebUI 的组件。能力包受依赖方向约束（只能引用
/// Abstractions + 原语），不可能持有宿主组件类型，因此这条贡献留在宿主侧，
/// 由 <c>WebUiHost</c> 以 <c>files.workspace</c> 作为 owner id 登记：
/// </para>
/// <code>
/// registry.AddRange("files.workspace", new FilePanelUiContribution().DescribeUi());
/// </code>
/// <para>
/// 归属仍由 <c>Requires = ["files.workspace"]</c> 表达，因此模块停用后面板自然消失，
/// 与会话工作模式收窄（扩展点 Scope = Session）叠加生效。
/// </para>
/// </summary>
public sealed class FilePanelUiContribution : IUiContributor
{
    /// <summary>归属模块 id（能力包 <c>Seeing.Agent.Files</c> 的模块 id）。</summary>
    public const string OwnerModuleId = "files.workspace";

    /// <summary>插槽扩展点 id。</summary>
    public const string PointId = "session.file.panel";

    /// <inheritdoc />
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new SlotContribution
        {
            Point = PointId,
            Id = "slot.file.panel",
            ViewKey = WebUiViewKeys.SessionFilePanel,
            Requires = [OwnerModuleId],
            DisplayName = "文件面板",
        },
    ];
}
