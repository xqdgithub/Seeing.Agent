using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 后台活动视图模型——<see cref="BackgroundActivitySnapshot"/> 的只读展示投影。
/// <para>
/// 活动事件不落盘，UI 首屏用 <c>IBackgroundActivityManager.List</c> 重建、实时增量靠
/// <see cref="BackgroundActivityEvent"/>；本类型只承载「展示需要」的派生文本，
/// 不参与任何框架决策（框架只读 <c>Capabilities</c>）。
/// </para>
/// </summary>
public sealed class BackgroundActivityViewModel
{
    /// <summary>活动 id（<c>act_</c> 前缀）。</summary>
    public required string Id { get; init; }

    /// <summary>归属会话 id（取消按钮据此鉴权）。</summary>
    public required string SessionId { get; init; }

    /// <summary>活动种类（开放字符串）。</summary>
    public required string Kind { get; init; }

    /// <summary>展示标题。</summary>
    public required string Title { get; init; }

    /// <summary>发起该活动的工具调用 id（卡片据此与工具调用关联）。</summary>
    public string? OriginToolCallId { get; init; }

    /// <summary>状态版本号：订阅方必须丢弃更旧的快照。</summary>
    public long Seq { get; init; }

    /// <summary>当前状态。</summary>
    public BackgroundActivityStatus Status { get; init; }

    /// <summary>能力位（唯一可驱动框架行为；本类型只用 <c>CanCancel</c> 决定是否出按钮）。</summary>
    public BackgroundActivityCapabilities Capabilities { get; init; }

    /// <summary>进度（仅事件携带，不落盘）。</summary>
    public BackgroundActivityProgress? Progress { get; init; }

    /// <summary>结果引用（不含输出正文）。</summary>
    public BackgroundActivityResult? Result { get; init; }

    /// <summary>输出上限（字节）。</summary>
    public long OutputLimitBytes { get; init; }

    /// <summary>kind 专属展示信息（只展示，不参与判定）。</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>();

    /// <summary>创建时刻。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>进入终态的时刻。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>是否为终态（终态不可再取消）。</summary>
    public bool IsTerminal => Status is BackgroundActivityStatus.Succeeded
        or BackgroundActivityStatus.Failed
        or BackgroundActivityStatus.Cancelled
        or BackgroundActivityStatus.Interrupted;

    /// <summary>是否可取消：能力位声明 + 未进终态。</summary>
    public bool CanCancel => !IsTerminal && Capabilities.HasFlag(BackgroundActivityCapabilities.CanCancel);

    /// <summary>状态文本（中文）。</summary>
    public string StatusText => Status switch
    {
        BackgroundActivityStatus.Pending => "等待",
        BackgroundActivityStatus.Running => "执行中",
        BackgroundActivityStatus.Succeeded => "完成",
        BackgroundActivityStatus.Failed => "失败",
        BackgroundActivityStatus.Cancelled => "已取消",
        BackgroundActivityStatus.Interrupted => "已中断",
        _ => Status.ToString()
    };

    /// <summary>状态标签颜色（AntDesign Tag Color）。</summary>
    public string StatusTagColor => Status switch
    {
        BackgroundActivityStatus.Pending => "warning",
        BackgroundActivityStatus.Running => "processing",
        BackgroundActivityStatus.Succeeded => "success",
        BackgroundActivityStatus.Failed => "error",
        _ => "default"
    };

    /// <summary>状态图标（AntDesign IconType）。</summary>
    public string StatusIcon => Status switch
    {
        BackgroundActivityStatus.Pending => "clock-circle",
        BackgroundActivityStatus.Running => "loading",
        BackgroundActivityStatus.Succeeded => "check-circle",
        BackgroundActivityStatus.Failed => "close-circle",
        _ => "stop"
    };

    /// <summary>进度百分比（0–100）；无比例时按单位数推导。</summary>
    public double? ProgressPercent
    {
        get
        {
            var progress = Progress;
            if (progress is null)
                return null;

            if (progress.Fraction is double fraction)
                return Math.Clamp(fraction * 100d, 0d, 100d);

            if (progress.UnitsDone is long done && progress.UnitsTotal is long total && total > 0)
                return Math.Clamp(done * 100d / total, 0d, 100d);

            return null;
        }
    }

    /// <summary>进度文本（比例 / 单位 / 说明）；无进度时为 null。</summary>
    public string? ProgressText
    {
        get
        {
            var progress = Progress;
            if (progress is null)
                return null;

            var parts = new List<string>(2);

            if (ProgressPercent is double percent)
                parts.Add($"{percent:F0}%");
            else if (progress.UnitsDone is long done)
                parts.Add(progress.UnitsTotal is long total
                    ? $"{done}/{total} {progress.UnitsName}".Trim()
                    : $"{done} {progress.UnitsName}".Trim());

            if (!string.IsNullOrWhiteSpace(progress.Message))
                parts.Add(progress.Message!);

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 输出大小文本。
    /// <para>
    /// 快照契约没有「已写字节数」字段（只有 <see cref="OutputLimitBytes"/>），因此只读取发起方
    /// 在 <see cref="Attributes"/> 里声明的展示键；没有时再看进度单位是否为字节。
    /// 都没有则返回 null（宁可不显示，也不猜）。
    /// </para>
    /// </summary>
    public string? OutputSizeText
    {
        get
        {
            foreach (var key in s_outputSizeKeys)
            {
                if (!Attributes.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                    continue;
                if (long.TryParse(raw, out var bytes) && bytes >= 0)
                    return FormatBytes(bytes);
            }

            if (Progress is { UnitsDone: long done, UnitsName: { } units } && IsByteUnit(units))
                return FormatBytes(done);

            return null;
        }
    }

    /// <summary>输出上限文本（仅在拿不到实际大小时作为兜底展示）。</summary>
    public string? OutputLimitText => OutputSizeText is null && OutputLimitBytes > 0
        ? $"上限 {FormatBytes(OutputLimitBytes)}"
        : null;

    /// <summary>错误信息（仅失败/中断时展示）。</summary>
    public string? ErrorMessage => Status is BackgroundActivityStatus.Failed
        or BackgroundActivityStatus.Interrupted
        ? Result?.ErrorMessage
        : null;

    private static readonly string[] s_outputSizeKeys =
    [
        "output_bytes",
        "output.size",
        "bytes_written",
        "size_bytes"
    ];

    /// <summary>从快照创建视图模型。</summary>
    public static BackgroundActivityViewModel FromSnapshot(BackgroundActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new BackgroundActivityViewModel
        {
            Id = snapshot.Id,
            SessionId = snapshot.OwnerSessionId,
            Kind = snapshot.Kind,
            Title = snapshot.Title,
            OriginToolCallId = snapshot.OriginToolCallId,
            Seq = snapshot.Seq,
            Status = snapshot.Status,
            Capabilities = snapshot.Capabilities,
            Progress = snapshot.Progress,
            Result = snapshot.Result,
            OutputLimitBytes = snapshot.OutputLimitBytes,
            Attributes = snapshot.Attributes ?? new Dictionary<string, string>(),
            CreatedAt = snapshot.CreatedAt,
            CompletedAt = snapshot.CompletedAt
        };
    }

    private static bool IsByteUnit(string units) => units.Trim().ToLowerInvariant() is
        "b" or "byte" or "bytes" or "kb" or "mb" or "gb";

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (bytes >= gb)
            return $"{bytes / gb:F2} GB";
        if (bytes >= mb)
            return $"{bytes / mb:F1} MB";
        if (bytes >= kb)
            return $"{bytes / kb:F1} KB";
        return $"{bytes} B";
    }
}
