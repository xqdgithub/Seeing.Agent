using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 审批模式文案与生效判定（对齐 WebUI 会话内分段控件「默认 / 自动 / 确认」）。
/// <para>
/// 生效规则与 <c>EffectivePermissionPolicy</c> 一致：会话三态 &gt; 全局 <c>Permission.AutoApproveAll</c>。
/// 子代理沿父会话链上溯的规则不在本类覆盖范围内（展示的是当前会话自身的设置）。
/// </para>
/// </summary>
internal static class AutoApproveText
{
    /// <summary>会话三态名称。</summary>
    public static string Mode(SessionAutoApprove mode) => mode switch
    {
        SessionAutoApprove.Enabled => "自动",
        SessionAutoApprove.Disabled => "确认",
        _ => "默认",
    };

    /// <summary>当前是否自动批准（Enabled→是；Disabled→否；FollowGlobal→随全局）。</summary>
    public static bool IsAuto(SessionAutoApprove mode, bool globalAutoApprove) => mode switch
    {
        SessionAutoApprove.Enabled => true,
        SessionAutoApprove.Disabled => false,
        _ => globalAutoApprove,
    };

    /// <summary>展示标签：<c>自动</c> / <c>确认</c>；跟随全局时加 <c>(全局)</c> 后缀标明来源。</summary>
    public static string Label(SessionAutoApprove mode, bool globalAutoApprove)
    {
        var effect = IsAuto(mode, globalAutoApprove) ? "自动" : "确认";
        return mode == SessionAutoApprove.FollowGlobal ? $"{effect}(全局)" : effect;
    }

    /// <summary>无参数时按三态循环（对齐 WebUI 分段控件的点击切换顺序）。</summary>
    public static SessionAutoApprove Next(SessionAutoApprove current) => current switch
    {
        SessionAutoApprove.FollowGlobal => SessionAutoApprove.Enabled,
        SessionAutoApprove.Enabled => SessionAutoApprove.Disabled,
        _ => SessionAutoApprove.FollowGlobal,
    };
}
