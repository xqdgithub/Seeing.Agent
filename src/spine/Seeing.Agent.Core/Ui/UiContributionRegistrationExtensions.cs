using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Core.Ui;

/// <summary>UI 扩展点的 DI 注册辅助。</summary>
public static class UiContributionRegistrationExtensions
{
    /// <summary>
    /// 声明宿主支持的扩展点。
    /// <para>
    /// <b>必须在 Build() 之前调用</b>：模块贡献在结算后收集，若此时目录为空，
    /// 所有贡献都会被判为「未声明的扩展点」而拒收 → UI 全空。
    /// </para>
    /// </summary>
    public static IServiceCollection AddSeeingUiPoints(
        this IServiceCollection services, params UiPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        services.TryAddSingleton<IUiPointCatalog>(_ => new UiPointCatalog(points));
        return services;
    }

    /// <summary>注册视图键目录（宿主的强类型解析实现通常同时注册为具体类型与接口）。</summary>
    public static IServiceCollection AddSeeingUiViewCatalog<T>(this IServiceCollection services)
        where T : class, IUiViewKeyCatalog
    {
        services.TryAddSingleton<T>();
        services.TryAddSingleton<IUiViewKeyCatalog>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
