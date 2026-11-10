using Microsoft.AspNetCore.Components;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>
/// 工作台视图表（<c>ViewKey → 组件类型</c>），宿主启动期登记、运行期只读。
/// <para>
/// <b>与信封的 <c>IUiViewKeyCatalog</c> 的关系</b>：那个接口回答「这个 ViewKey 有没有视图」，
/// 由宿主的通用视图表 <c>WebUiViewResolver</c> 实现。本类<b>不</b>另建一套键空间——
/// 它只是把工作台组件的<b>契约校验</b>（必须声明 <c>WorkbenchViewContext</c> 参数）附加在登记时，
/// 这样「声明了工作台但没有可用组件」在启动期就变成硬失败，而不是用户点开看到空白。
/// </para>
/// <para>因此：登记走 <c>WebUiViewResolver.Register(key, type)</c>，本类负责守门。</para>
/// </summary>
public sealed class WorkbenchViewRegistry
{
    private readonly Dictionary<string, Type> _views = new(StringComparer.Ordinal);
    private readonly List<string> _problems = [];
    private readonly object _gate = new();

    /// <summary>登记一个工作台视图（启动期调用）。</summary>
    /// <param name="viewKey">视图键。</param>
    /// <param name="componentType">组件类型。</param>
    /// <exception cref="InvalidOperationException">
    /// 键重复 / 类型不是组件 / 组件未声明 <c>WorkbenchViewContext</c> 参数。
    /// </exception>
    public void Register(string viewKey, Type componentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewKey);
        ArgumentNullException.ThrowIfNull(componentType);

        if (!typeof(IComponent).IsAssignableFrom(componentType))
            throw new InvalidOperationException(
                $"工作台视图 '{viewKey}' 的类型 '{componentType.FullName}' 未实现 {nameof(IComponent)}");

        var parameter = componentType
            .GetProperties()
            .FirstOrDefault(property =>
                property.PropertyType == typeof(WorkbenchViewContext)
                && property.GetCustomAttributes(typeof(ParameterAttribute), inherit: true).Length > 0);

        if (parameter is null)
            throw new InvalidOperationException(
                $"工作台视图 '{viewKey}' 的组件 '{componentType.FullName}' 未声明 " +
                $"[Parameter] {nameof(WorkbenchViewContext)} 参数；工作台无法接收会话与打开载荷");

        lock (_gate)
        {
            if (_views.TryGetValue(viewKey, out var existing))
                throw new InvalidOperationException(
                    $"重复的工作台视图键 '{viewKey}'：已登记 {existing.FullName}，又收到 {componentType.FullName}");

            _views[viewKey] = componentType;
        }
    }

    /// <summary>解析视图键；未登记返回 null。</summary>
    public Type? Resolve(string? viewKey)
    {
        if (string.IsNullOrWhiteSpace(viewKey))
            return null;

        lock (_gate)
            return _views.GetValueOrDefault(viewKey);
    }

    /// <summary>已登记的工作台视图键。</summary>
    public IReadOnlyCollection<string> Keys
    {
        get { lock (_gate) return _views.Keys.ToArray(); }
    }

    /// <summary>登记过程的诊断（空 = 全部合格）。</summary>
    public IReadOnlyList<string> Problems
    {
        get { lock (_gate) return _problems.ToArray(); }
    }
}
