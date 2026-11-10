using Microsoft.AspNetCore.Components;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.WebUI.Shell;

/// <summary>
/// 视图键 → 组件类型（宿主侧强类型解析）。
/// <para>
/// 契约层只承诺「<see cref="ViewKey"/> 是个字符串」，具体解析留在宿主：能力包不引用本程序集，
/// 因此也不可能在编译期持有组件类型。
/// </para>
/// <para>
/// 线程安全：启动期单线程注册，运行期只读。注册后仍用锁保护，避免宿主并行装配时撕裂字典。
/// </para>
/// </summary>
public sealed class WebUiViewResolver : IUiViewKeyCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Type> _views = new(StringComparer.Ordinal);

    /// <summary>
    /// 登记 <paramref name="viewKey"/> → <paramref name="componentType"/>。
    /// </summary>
    /// <param name="viewKey">视图键（约定与 <c>UiContribution.Id</c> 相同）。</param>
    /// <param name="componentType">组件类型；必须实现 <see cref="IComponent"/>。</param>
    /// <exception cref="ArgumentException"><paramref name="viewKey"/> 为空。</exception>
    /// <exception cref="InvalidOperationException">重复键，或类型不是组件。</exception>
    public void Register(string viewKey, Type componentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewKey);
        ArgumentNullException.ThrowIfNull(componentType);

        if (!typeof(IComponent).IsAssignableFrom(componentType))
        {
            throw new InvalidOperationException(
                $"视图 '{viewKey}' 的类型 '{componentType.FullName}' 未实现 {nameof(IComponent)}；" +
                "DynamicComponent 会拒绝渲染它");
        }

        lock (_gate)
        {
            if (_views.TryGetValue(viewKey, out var existing))
            {
                throw new InvalidOperationException(
                    $"重复的视图键 '{viewKey}'：已登记 {existing.FullName}，又收到 {componentType.FullName}。" +
                    "重复键会让同一路由的渲染目标取决于注册顺序");
            }

            _views[viewKey] = componentType;
        }
    }

    /// <summary>解析视图键；未登记返回 null（消费方据此降级）。</summary>
    public Type? Resolve(string? viewKey)
    {
        if (string.IsNullOrWhiteSpace(viewKey))
            return null;

        lock (_gate)
            return _views.GetValueOrDefault(viewKey);
    }

    /// <inheritdoc />
    public bool CanResolve(string viewKey) => Resolve(viewKey) is not null;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_gate)
                return _views.Keys.ToArray();
        }
    }
}
