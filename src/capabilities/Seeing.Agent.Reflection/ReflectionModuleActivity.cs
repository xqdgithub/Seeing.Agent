namespace Seeing.Agent.Reflection;

/// <summary>
/// 复盘模块运行门控 — Activate/Deactivate 与后台 Worker / 终态观察者共享。
/// <para>
/// Activate 置 <c>true</c>，Deactivate 置 <c>false</c>；后台循环与观察者据此跳过工作。
/// </para>
/// </summary>
public sealed class ReflectionModuleActivity
{
    // 默认 true：宿主只调用 AddSeeingReflection（未走模块生命周期）时后台仍可运行；
    // 显式 Deactivate 才翻为 false。
    private volatile bool _isActive = true;

    /// <summary>模块是否处于激活运行态。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => _isActive = value;
    }
}
