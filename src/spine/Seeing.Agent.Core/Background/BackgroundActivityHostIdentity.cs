using System.Diagnostics;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 宿主实例标识（<c>pid:进程启动时间Ticks</c>）的唯一实现：生成当前实例标识 + 判定任意标识是否存活。
/// <para>
/// manager（写快照 / 孤儿收敛）与 outbox（写批次、判「是否本实例」、清理死实例 pending）
/// 必须解出同一套语义，否则自己写的 pending 会被自己当成「别的实例」而永不投递，
/// 或把已崩溃宿主留下的批次一直挂在盘上。
/// </para>
/// </summary>
internal static class BackgroundActivityHostIdentity
{
    /// <summary>当前进程的宿主实例标识。</summary>
    internal static string Current { get; } = Build();

    /// <summary>
    /// 判定 <paramref name="hostInstanceId"/>（<c>pid:startTicks</c>）对应的宿主是否仍存活。
    /// <para>
    /// 同 pid 但启动时间不同 → 视为已死：Windows 会复用 pid，只看 pid 会把新进程
    /// 当成那个已经崩溃的旧宿主。
    /// </para>
    /// </summary>
    internal static bool IsAlive(string? hostInstanceId)
    {
        if (string.IsNullOrEmpty(hostInstanceId))
            return false;

        // 本实例不需要探测（自己的 pid 不可能被复用），也避免受限环境下 GetProcessById 异常反转判定。
        if (string.Equals(hostInstanceId, Current, StringComparison.Ordinal))
            return true;

        var separator = hostInstanceId.IndexOf(':');
        if (separator <= 0)
            return false;

        if (!int.TryParse(hostInstanceId.AsSpan(0, separator), out var pid) || pid <= 0)
            return false;

        if (!long.TryParse(hostInstanceId.AsSpan(separator + 1), out var startTicks))
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;

            // pid-only 退化记录（启用时拿不到启动时间）：只能按 pid 判定，宁可漏收敛也不误杀。
            if (startTicks == 0)
                return true;

            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException)
        {
            // 该 pid 不存在：宿主已死。
            return false;
        }
        catch (InvalidOperationException)
        {
            // 进程在探测窗口内退出。
            return false;
        }
        catch (Exception)
        {
            // 访问受限等无法判定：保守视为存活，避免把在跑的活动改写为 Interrupted。
            return true;
        }
    }

    private static string Build()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}";
        }
        catch (Exception)
        {
            // 受限环境拿不到进程启动时间：退化为 pid-only（仍可区分同机不同进程）。
            return $"{Environment.ProcessId}:0";
        }
    }
}
