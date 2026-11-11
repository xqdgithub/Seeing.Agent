using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Files.Workbenches;
using Seeing.Agent.WebUI.Workbench;

namespace Seeing.Agent.WebUI.Shell;

/// <summary>
/// 工作台视图登记（<c>ViewKey → Type</c>）。唯一知道工作台组件类型的地方。
/// <para>
/// <b>为什么不放进 <see cref="WebUiViews"/></b>：那个类是页面/设置卡/插槽的通用视图表；
/// 工作台有自己的契约要求（组件必须声明 <c>WorkbenchViewContext</c> 参数），
/// 由 <c>WorkbenchViewRegistry</c> 在登记时硬校验，混进通用表会让校验失去落点。
/// </para>
/// <para>
/// <b>为什么在宿主层</b>：能力包 <c>Seeing.Agent.Files</c> 只声明 <c>ViewKey</c> 字符串，
/// 受依赖方向约束不能持有这里的组件类型。
/// </para>
/// </summary>
public static class WebUiWorkbenchViews
{
    /// <summary>登记全部内置工作台视图，并同步到宿主的通用视图表（信封校验需要能解析）。</summary>
    /// <param name="registry">工作台视图表（带契约校验）。</param>
    /// <param name="views">宿主通用视图表（<c>IUiViewKeyCatalog</c> 的实现）。</param>
    public static void RegisterBuiltIns(WorkbenchViewRegistry registry, WebUiViewResolver views)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(views);

        // markdown 已合并进 files（preview 载荷键 + 标签栏切换），历史 id 由
        // LegacyWorkbenchAlias 在宿主侧别名为 files + preview=true。
        Register(registry, views, "files", typeof(FilesWorkbench));
    }

    private static void Register(
        WorkbenchViewRegistry registry, WebUiViewResolver views, string workbenchId, Type componentType)
    {
        var viewKey = WorkbenchPoints.ViewKeyFor(workbenchId);

        // 先做工作台契约校验（必须声明 WorkbenchViewContext 参数），再进通用视图表
        registry.Register(viewKey, componentType);
        views.Register(viewKey, componentType);
    }
}
