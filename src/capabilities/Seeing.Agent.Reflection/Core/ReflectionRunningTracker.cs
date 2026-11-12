using Seeing.Agent.Reflection.Abstractions;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 复盘「正在执行」的当前项登记（进程内单槽）。
/// <para>
/// <b>为什么单独一个类型而不塞进队列</b>：队列的语义是「还没开始跑」，
/// 而 Worker 用 <see cref="IReflectionQueue.TryDequeue"/> 取出请求后，该会话就已经不在队列里了。
/// 把「执行中」混进队列会让 <c>PendingCount</c> 把正在跑的那个也算成排队，
/// 用户看到「排队 1 个」但实际什么都没在等。
/// </para>
/// <para>
/// 单槽是刻意的：<see cref="Background.ReflectionWorker"/> 是单循环，全进程同时最多一个复盘在跑。
/// </para>
/// </summary>
public sealed class ReflectionRunningTracker
{
    private volatile ReflectionRequest? _current;

    /// <summary>当前正在执行的请求；空闲时 <c>null</c>。</summary>
    public ReflectionRequest? Current => _current;

    /// <summary>标记开始执行。</summary>
    /// <param name="request">即将执行的请求。</param>
    public void Begin(ReflectionRequest request) => _current = request;

    /// <summary>标记结束（无论成功、跳过还是失败）。</summary>
    /// <param name="request">刚结束的请求。</param>
    /// <remarks>
    /// 按值比对再清：Worker 结束后可能已开始下一个，无条件置 null 会把新项抹掉。
    /// </remarks>
    public void End(ReflectionRequest request)
    {
        if (Equals(_current, request))
            _current = null;
    }
}
