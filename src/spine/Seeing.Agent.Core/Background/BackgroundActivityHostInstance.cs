using System.Diagnostics;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 宿主实例标识的唯一来源：<c>pid:进程启动时间(Ticks)</c>。
/// <para>
/// manager（写快照）与 outbox（写批次、判「是否本实例」）必须解出同一个值，否则
/// 自己写的 pending 会被自己当成「别的实例」而永不投递。因此只在这里算一次，
/// 其余位置一律读 <see cref="Current"/>。
/// </para>
/// </summary>
internal static class BackgroundActivityHostInstance
{
    /// <summary>当前进程的宿主实例标识。</summary>
    internal static string Current { get; } = Build();

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
