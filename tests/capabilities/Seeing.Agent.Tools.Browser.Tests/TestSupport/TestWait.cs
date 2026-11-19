using System.Diagnostics;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 测试用的条件等待：轮询直到条件成立或超时。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不写 <c>Task.Delay(固定时长)</c></b>：固定等待既慢（每个用例都在空转）
/// 又不可靠（机器负载高时仍然失败）。这里命中条件即返回，超时才失败，
/// 等待时长只是上限而不是实际耗时。
/// </para>
/// <para>
/// 本类里的 <see cref="PollInterval"/> 属于「轮询间隔」这一类固定等待：
/// 它是实现细节而不是断言窗口，命中即返回。
/// </para>
/// </remarks>
internal static class TestWait
{
    /// <summary>轮询间隔（实现细节，非断言窗口）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 轮询直到 <paramref name="condition"/> 成立；超时返回 <see langword="false"/>。
    /// </summary>
    /// <param name="condition">条件（可能抛异常，视作「尚未成立」以外的情况会向上抛）。</param>
    /// <param name="timeout">最长等待时长。</param>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (condition())
                return true;

            if (stopwatch.Elapsed >= timeout)
                return false;

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }
}
