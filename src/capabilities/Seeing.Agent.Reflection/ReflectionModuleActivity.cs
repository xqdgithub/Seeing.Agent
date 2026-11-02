namespace Seeing.Agent.Reflection;

/// <summary>
/// 复盘模块运行门控 — Activate/Deactivate 与后台 Worker / 终态观察者共享。
/// <para>
/// Activate 置 <c>true</c>，Deactivate 置 <c>false</c>；后台循环与观察者据此跳过工作。
/// </para>
/// </summary>
public sealed class ReflectionModuleActivity
{
    // 默认 false：只有模块真正 Activate 过才允许后台干活。
    // 若默认 true，Boot 收窄的能力集（不含 reflection 模块）会出现「worker 在跑但
    // reflection agent 从未注册」——每轮先建子会话再因 Agent not found 失败，孤儿会话累积。
    private volatile bool _isActive;

    /// <summary>模块是否处于激活运行态。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => _isActive = value;
    }
}
