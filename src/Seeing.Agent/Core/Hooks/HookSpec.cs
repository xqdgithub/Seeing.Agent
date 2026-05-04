namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 规范（点 + 策略）
/// </summary>
public record HookSpec(HookPolicy Policy, string Point)
{
    /// <summary>默认规范</summary>
    public static readonly HookSpec Default = new(HookPolicy.Blocking, "default");
    
    /// <summary>隐式转换为 Hook 点字符串</summary>
    public static implicit operator string(HookSpec spec) => spec.Point;
}