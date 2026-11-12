using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Abstractions;

/// <summary>
/// 复盘运行态只读快照（供设置卡/记录页展示「配了到底有没有生效」）。
/// <para>
/// 存在的理由：复盘是默认关闭的后台能力，用户开启后没有任何即时反馈——
/// 不知道模块是否激活、产物写到哪里、今天烧了多少预算、队列里有没有在等、
/// 正在跑的是哪一个。这些事实本来散在多个内部服务里，收敛成一个只读端口后，
/// UI 不必逐项解析内部类型。
/// </para>
/// </summary>
public interface IReflectionStatus
{
    /// <summary>模块是否已激活（启用集里含 <c>reflection</c> 且 Activate 成功）。</summary>
    bool IsModuleActive { get; }

    /// <summary>复盘产物根目录（journal 与用量账本的落盘位置）。</summary>
    string StorageDirectory { get; }

    /// <summary>今日（UTC）已消耗的复盘 Token 总量。</summary>
    long UsedTodayTokens { get; }

    /// <summary>当前排队等待复盘的会话数。</summary>
    int PendingCount { get; }

    /// <summary>
    /// 当前排队中的请求快照（按请求产生时刻升序）。
    /// <para>用户点完「立即复盘」后需要看到「它真的在队列里」，否则按钮与无操作无从区分。</para>
    /// </summary>
    IReadOnlyList<ReflectionRequest> Queued { get; }

    /// <summary>
    /// 正在执行中的请求；空闲时为 <c>null</c>。
    /// <para>
    /// 与 <see cref="Queued"/> 互斥：请求一旦被 Worker 取出就从队列消失、进入这里，
    /// 因此「排队中 + 执行中」正好覆盖「还没出结果的全部工作」。
    /// </para>
    /// </summary>
    ReflectionRequest? Running { get; }
}

