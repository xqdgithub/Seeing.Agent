namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 图标 token → AntDesign 图标名映射。
/// <para>
/// <see cref="Seeing.Agent.Abstractions.Ui.NavPlacement.Icon"/> 是<b>中性 token</b>（能力包不知道
/// 宿主用哪个图标库），由宿主在这里翻译。迁移前 <c>AppSidebar.razor</c> 直接把 token 喂给
/// <c>MenuItem.Icon</c>，靠「恰好同名」工作——显式映射表消除这个隐式耦合。
/// </para>
/// <para>
/// 未登记的 token <b>原样返回</b>：第三方模块自造 token 时至少还能渲染出同名图标，
/// 而不是整条导航项消失（可见性由权限/模块决定，不该被图标拖累）。
/// </para>
/// </summary>
public static class WebUiIconMap
{
    private static readonly Dictionary<string, string> s_map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 分组
        ["control"] = "control",
        ["folder"] = "folder",
        ["setting"] = "setting",

        // 页面
        ["database"] = "database",
        ["apartment"] = "apartment",
        ["file"] = "file",
        ["clock-circle"] = "clock-circle",
        ["heart"] = "heart",
        ["api"] = "api",
        ["robot"] = "robot",
        ["global"] = "global",
        ["star"] = "star",
        ["tool"] = "tool",
        ["bulb"] = "bulb",
        ["team"] = "team",
        ["usergroup-add"] = "usergroup-add",
        ["edit"] = "edit",
        ["cloud-server"] = "cloud-server",
        ["lock"] = "lock",
    };

    /// <summary>解析图标 token；空串返回 <c>""</c>（调用方自行决定是否渲染）。</summary>
    public static string Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var key = token.Trim();
        return s_map.TryGetValue(key, out var name) ? name : key;
    }

    /// <summary>解析可空图标 token；空/空白返回 null（表示「不渲染图标」）。</summary>
    public static string? ResolveOptional(string? token)
        => string.IsNullOrWhiteSpace(token) ? null : Resolve(token);
}
