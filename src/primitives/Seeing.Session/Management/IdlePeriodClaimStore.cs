using System.Collections.Concurrent;

namespace Seeing.Session.Management;

/// <summary>
/// 空闲期占位登记：记录「哪些会话的<b>哪一段</b>空闲期已经被本消费方处理过」。
/// <para>
/// <b>为什么需要它（而不是调用 <see cref="ISessionActivityTracker.Clear"/>）</b>：
/// 活跃度追踪器存的是<b>事实</b>（会话最后一次活动时刻），它被多个后台消费方共享
/// （记忆抽取、复盘触发……）。消费方处理完一次空闲期后调用 <c>Clear</c>，
/// 等于把「事实」删掉——其它消费方再也看不到这个会话已经空闲过。
/// 实测后果：记忆演化默认 15 分钟处理并清理时间戳，复盘默认 30 分钟，
/// 于是复盘的 <c>GetIdleSessions(30min)</c> 永远返回空，空闲触发<b>永久失效</b>。
/// </para>
/// <para>
/// 正确分工是：<b>事实不可被消费方删除</b>，去重状态归消费方各持一份。
/// 键用「活动时刻」而不是「上次处理时刻」：用户一旦回到会话，
/// <see cref="ISessionActivityTracker.Touch"/> 会刷新时刻，本类的旧条目自动失配，
/// 天然允许下一次空闲处理。
/// </para>
/// <para>
/// <b>必须每个消费方一个实例</b>（不能注册成 DI 单例）：共享会让一个消费方的处理
/// 把另一个消费方的机会吃掉，问题比 <c>Clear</c> 更隐蔽。
/// </para>
/// </summary>
public sealed class IdlePeriodClaimStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _claims = new(StringComparer.Ordinal);

    /// <summary>该会话的<b>这一段</b>空闲期是否已被处理过。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="lastActivity">本段空闲期对应的活动时刻（来自追踪器）。</param>
    public bool IsClaimed(string sessionId, DateTimeOffset lastActivity)
        => !string.IsNullOrWhiteSpace(sessionId)
           && _claims.TryGetValue(sessionId, out var claimed)
           && claimed == lastActivity;

    /// <summary>登记「这一段空闲期已处理」。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="lastActivity">本段空闲期对应的活动时刻。</param>
    public void Claim(string sessionId, DateTimeOffset lastActivity)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _claims[sessionId] = lastActivity;
    }

    /// <summary>
    /// 撤销登记。用于「已经占位但最终没能处理」的路径（如队列已满），
    /// 否则该会话会被本消费方永久跳过，等队列空出来也不会再试。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    public void Release(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _claims.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// 清理已失效的登记：会话消失（追踪器里没有）或其活动时刻已变（用户回来了）。
    /// <para>不做会随进程内的会话总数无界增长——这正是原先 <c>Clear</c> 被滥用的原因。</para>
    /// </summary>
    /// <param name="lookupLastActivity">按会话 ID 读取当前活动时刻；不存在返回 <c>null</c>。</param>
    public void Prune(Func<string, DateTimeOffset?> lookupLastActivity)
    {
        ArgumentNullException.ThrowIfNull(lookupLastActivity);

        foreach (var pair in _claims)
        {
            var current = lookupLastActivity(pair.Key);
            if (current is null || current.Value != pair.Value)
                _claims.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>当前登记条数（诊断 / 测试用）。</summary>
    public int Count => _claims.Count;
}
