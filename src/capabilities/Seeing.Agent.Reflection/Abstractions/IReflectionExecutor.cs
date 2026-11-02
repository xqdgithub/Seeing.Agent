using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Abstractions;

/// <summary>
/// 复盘执行器端口：用请求描述的会话做一次回顾分析。
/// <para>
/// 实现为 <see cref="Core.ReflectionExecutor"/>。所有「没做」的原因都通过
/// <see cref="ReflectionRunResult"/> 返回，端口刻意不声明会抛出的业务异常，
/// 以免后台 Worker 需要靠猜异常类型来区分跳过与故障。
/// </para>
/// </summary>
public interface IReflectionExecutor
{
    /// <summary>执行一次复盘。</summary>
    /// <param name="request">复盘请求。</param>
    /// <param name="ct">取消令牌（仅承载宿主停止信号，业务超时由实现自行施加）。</param>
    Task<ReflectionRunResult> RunAsync(ReflectionRequest request, CancellationToken ct = default);
}
