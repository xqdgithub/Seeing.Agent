namespace Seeing.Agent.WebUI.Services;

/// <summary>模块路由解析结果。</summary>
public enum ModuleRouteKind
{
    /// <summary>注册表命中且可渲染。</summary>
    Found,

    /// <summary>已知模块页但未启用 / 视图未装配。</summary>
    NotEnabled,

    /// <summary>未知路径（ModuleRouter 渲染友好未找到提示）。</summary>
    Fallback,
}

/// <summary>一次路径解析结果（<c>ModuleRouter</c> 的渲染输入）。</summary>
/// <param name="Kind">结果种类。</param>
/// <param name="ComponentType">命中时的组件类型（<see cref="ModuleRouteKind.Found"/> 必非 null）。</param>
/// <param name="Parameters">路由参数（键逐字等于模板里的占位名）。</param>
/// <param name="FeatureTitle">功能显示名（未命中时的友好文案用）。</param>
/// <param name="ModuleId">提供该页的模块 id（未启用提示据此建议可切换的工作模式）。</param>
public sealed record ModuleRouteResolution(
    ModuleRouteKind Kind,
    Type? ComponentType = null,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? FeatureTitle = null,
    string? ModuleId = null);
