namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Bootstrap 与 <see cref="MemoryExtension"/> 共用的 Hook 注册闸门，保证进程内只注册一次。
/// </summary>
internal static class MemoryHookRegistrationGate
{
    private static int s_claimed;

    /// <returns>true 表示当前调用方应执行注册。</returns>
    public static bool TryClaim() => Interlocked.Exchange(ref s_claimed, 1) == 0;
}
