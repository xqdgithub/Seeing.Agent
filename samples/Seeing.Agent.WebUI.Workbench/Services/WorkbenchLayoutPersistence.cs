using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Ui.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>
/// 工作台布局持久化（localStorage，按会话分桶）。
/// <para>
/// <b>只持久化 UI 形态</b>：开了哪些工作台、激活哪个、dock 宽度。<b>不</b>持久化
/// <c>OpenPayload</c>（一次性指令）与工作台内部内容（那是工作台自己的 <c>ViewState</c> 职责）。
/// 刷新后成员与宽度恢复，内容由各工作台用 <c>ViewState</c> 重建自己。
/// </para>
/// <para>所有 JS 错误在此吞掉并记 Debug：localStorage 不可用（隐私模式 / 配额满）不得阻断会话页。</para>
/// </summary>
public sealed class WorkbenchLayoutPersistence
{
    /// <summary>localStorage 键前缀。</summary>
    public const string KeyPrefix = "seeing.workbench.";

    /// <summary>
    /// 快照 schema 版本；变化时旧数据整体丢弃（不做迁移）。
    /// <para>
    /// v2：v1 快照记录的宽度是 35% 的默认值（一次错误的默认值调整），
    /// 属于「框架写的」而不是用户拖出来的偏好，因此整体作废重来。
    /// </para>
    /// </summary>
    public const int SchemaVersion = 2;

    private readonly IJSRuntime _js;
    private readonly ILogger<WorkbenchLayoutPersistence>? _logger;

    /// <summary>创建持久化。</summary>
    public WorkbenchLayoutPersistence(IJSRuntime js, ILogger<WorkbenchLayoutPersistence>? logger = null)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _logger = logger;
    }

    /// <summary>读取某会话的布局快照；不存在 / 损坏 / 版本不符返回 null。</summary>
    public async Task<WorkbenchLayoutSnapshot?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        string? raw;
        try
        {
            raw = await _js.InvokeAsync<string?>("localStorage.getItem", ct, KeyPrefix + sessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "读取工作台布局失败（忽略）：{SessionId}", sessionId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<WorkbenchLayoutSnapshot>(raw);
            if (snapshot is null || snapshot.Version != SchemaVersion)
            {
                _logger?.LogDebug("工作台布局快照版本不符，已丢弃：{SessionId}", sessionId);
                return null;
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            // 脏数据：丢弃该会话布局，回到默认（不阻塞、不弹错）
            _logger?.LogWarning(ex, "工作台布局快照解析失败，已丢弃：{SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>写入某会话的布局快照。</summary>
    public async Task SaveAsync(string sessionId, WorkbenchLayoutSnapshot snapshot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || snapshot is null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(snapshot with { Version = SchemaVersion });
            await _js.InvokeVoidAsync("localStorage.setItem", ct, KeyPrefix + sessionId, json);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "写入工作台布局失败（忽略）：{SessionId}", sessionId);
        }
    }

    /// <summary>删除某会话的布局快照（会话删除 / 布局清空）。</summary>
    public async Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", ct, KeyPrefix + sessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "删除工作台布局失败（忽略）：{SessionId}", sessionId);
        }
    }
}

/// <summary>
/// 持久化快照（DTO）：只含可恢复的 UI 形态。
/// </summary>
public sealed record WorkbenchLayoutSnapshot
{
    /// <summary>schema 版本（与 <see cref="WorkbenchLayoutPersistence.SchemaVersion"/> 比对）。</summary>
    public int Version { get; init; } = WorkbenchLayoutPersistence.SchemaVersion;

    /// <summary>左 dock 成员（按打开顺序）。</summary>
    public IReadOnlyList<WorkbenchLayoutItem> Left { get; init; } = [];

    /// <summary>右 dock 成员（按打开顺序）。</summary>
    public IReadOnlyList<WorkbenchLayoutItem> Right { get; init; } = [];

    /// <summary>左 dock 激活的工作台 id；null = 取第一个。</summary>
    public string? ActiveLeft { get; init; }

    /// <summary>右 dock 激活的工作台 id；null = 取第一个。</summary>
    public string? ActiveRight { get; init; }

    /// <summary>左 dock 宽度百分比。</summary>
    public double LeftWidthPercent { get; init; } = WorkbenchSizing.Default.DefaultPercent;

    /// <summary>右 dock 宽度百分比。</summary>
    public double RightWidthPercent { get; init; } = WorkbenchSizing.Default.DefaultPercent;

    /// <summary>聊天主列是否隐藏（工作区独占全宽）。</summary>
    public bool ChatHidden { get; init; }

    /// <summary>从内存布局构造快照。</summary>
    public static WorkbenchLayoutSnapshot From(string sessionId, WorkbenchSessionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return new WorkbenchLayoutSnapshot
        {
            Left = layout.Left.Select(WorkbenchLayoutItem.From).ToArray(),
            Right = layout.Right.Select(WorkbenchLayoutItem.From).ToArray(),
            ActiveLeft = layout.ActiveLeft is null ? null : WorkbenchInstanceKey.TryParse(layout.ActiveLeft, out var left) ? left.WorkbenchId : null,
            ActiveRight = layout.ActiveRight is null ? null : WorkbenchInstanceKey.TryParse(layout.ActiveRight, out var right) ? right.WorkbenchId : null,
            LeftWidthPercent = layout.LeftWidthPercent,
            RightWidthPercent = layout.RightWidthPercent,
            ChatHidden = layout.ChatHidden
        };
    }
}

/// <summary>快照中的单个工作台条目。</summary>
public sealed record WorkbenchLayoutItem
{
    /// <summary>工作台 id。</summary>
    public required string WorkbenchId { get; init; }

    /// <summary>可恢复小状态（工作台上报）。</summary>
    public IReadOnlyDictionary<string, string>? ViewState { get; init; }

    internal static WorkbenchLayoutItem From(WorkbenchInstance instance) => new()
    {
        WorkbenchId = instance.Key.WorkbenchId,
        ViewState = instance.ViewState
    };
}
