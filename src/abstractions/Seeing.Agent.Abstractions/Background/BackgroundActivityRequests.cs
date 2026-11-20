using System.Text.Json;

namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 启动后台活动请求（会话面入口）。
/// <para>
/// <see cref="OwnerSessionId"/> 必须等于调用方传入的 <c>requesterSessionId</c>，
/// 否则 manager 抛 <see cref="UnauthorizedAccessException"/>。
/// </para>
/// </summary>
public sealed record BackgroundActivityStartRequest
{
    /// <summary>活动种类（开放字符串，带命名空间前缀）。</summary>
    public required string Kind { get; init; }

    /// <summary>展示标题。</summary>
    public required string Title { get; init; }

    /// <summary>归属会话 id。</summary>
    public required string OwnerSessionId { get; init; }

    /// <summary>归属执行 id。</summary>
    public required string OwnerExecutionId { get; init; }

    /// <summary>发起该活动的工具调用 id。</summary>
    public string? OriginToolCallId { get; init; }

    /// <summary>父活动 id。</summary>
    public string? ParentActivityId { get; init; }

    /// <summary>目标会话 id。</summary>
    public string? TargetSessionId { get; init; }

    /// <summary>工作目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// kind 专属入参；由发起方与 launcher 约定，框架不解释。
    /// <para>一律经 <c>JsonSerializer.SerializeToElement</c> 构造并自持 <see cref="JsonDocument"/>。</para>
    /// </summary>
    public JsonElement? Payload { get; init; }

    /// <summary>发起来源。</summary>
    public BackgroundActivityOrigin Origin { get; init; } = BackgroundActivityOrigin.ToolBackground;

    /// <summary>可见范围。</summary>
    public BackgroundActivityVisibility Visibility { get; init; } = BackgroundActivityVisibility.Session;

    /// <summary>完成通知策略。</summary>
    public BackgroundActivityNotifyPolicy NotifyPolicy { get; init; } = BackgroundActivityNotifyPolicy.OnComplete;

    /// <summary>活动超时。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>输出上限（字节）；为空取配置默认值。</summary>
    public long? OutputLimitBytes { get; init; }

    /// <summary>生命周期边界。</summary>
    public BackgroundActivityLifetime Lifetime { get; init; } = BackgroundActivityLifetime.Host;

    /// <summary>去重键（由调用方计算，manager 只比字符串，绝不读 <see cref="Payload"/>）。</summary>
    public string? DedupeKey { get; init; }

    /// <summary>去重策略。</summary>
    public BackgroundActivityDedupeStrategy DedupeStrategy { get; init; } = BackgroundActivityDedupeStrategy.Reject;

    /// <summary>展示与诊断信息；框架决策不得读取。</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 登记一条已由外部机制启动的后台活动（系统面入口，例如模块自管的后台运行）。
/// </summary>
public sealed record BackgroundActivityTrackRequest
{
    /// <summary>活动种类。</summary>
    public required string Kind { get; init; }

    /// <summary>展示标题。</summary>
    public required string Title { get; init; }

    /// <summary>归属会话 id。</summary>
    public required string OwnerSessionId { get; init; }

    /// <summary>归属执行 id。</summary>
    public required string OwnerExecutionId { get; init; }

    /// <summary>父活动 id。</summary>
    public string? ParentActivityId { get; init; }

    /// <summary>目标会话 id。</summary>
    public string? TargetSessionId { get; init; }

    /// <summary>发起来源（登记默认为模块）。</summary>
    public BackgroundActivityOrigin Origin { get; init; } = BackgroundActivityOrigin.Module;

    /// <summary>能力位。</summary>
    public BackgroundActivityCapabilities Capabilities { get; init; }

    /// <summary>完成通知策略（登记默认不通知）。</summary>
    public BackgroundActivityNotifyPolicy NotifyPolicy { get; init; } = BackgroundActivityNotifyPolicy.None;

    /// <summary>二选一：宿主提供完成信号，或宿主自行通过 <c>handle.Reporter</c> 上报终态。</summary>
    public Task? Completion { get; init; }

    /// <summary>展示与诊断信息；框架决策不得读取。</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

/// <summary>活动查询条件（<see cref="IBackgroundActivityManager.List"/> 专用）。</summary>
public sealed record BackgroundActivityQuery(
    BackgroundActivityStatus? Status = null,
    string? Kind = null,
    bool IncludeCompleted = true);

/// <summary>等待活动的结果。</summary>
public sealed record BackgroundActivityWaitResult(
    BackgroundActivityWaitOutcome Outcome,
    BackgroundActivitySnapshot? Snapshot);

/// <summary>输出分页（服务端单页最多 32KB）。</summary>
public sealed record BackgroundActivityOutputPage(
    string Content,
    long NextOffset,
    bool EndOfStream,
    bool Truncated);
