namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 执行结果
/// </summary>
public record HookResult(bool Continue, Exception? Error = null)
{
    public static readonly HookResult Success = new(Continue: true);
    public static readonly HookResult Stop = new(Continue: false);
    public static HookResult FromError(Exception ex) => new(Continue: false, Error: ex);
}