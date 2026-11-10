using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Ui.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>
/// 传给工作台组件的唯一参数对象。
/// <para>
/// 工作台组件只需声明 <c>[Parameter] public WorkbenchViewContext Context { get; set; }</c>，
/// 不必知道 dock 布局、宿主请求通道、持久化等任何框架细节。
/// </para>
/// </summary>
public sealed class WorkbenchViewContext
{
    /// <summary>会话 id。</summary>
    public required string SessionId { get; init; }

    /// <summary>实例键（dock + 工作台 id）。</summary>
    public required WorkbenchInstanceKey Key { get; init; }

    /// <summary>工作台声明。</summary>
    public required WorkbenchContribution Descriptor { get; init; }

    /// <summary>打开载荷（一次性指令；刷新后为空）。</summary>
    public JsonElement? OpenPayload { get; init; }

    /// <summary>上次上报的可恢复状态。</summary>
    public IReadOnlyDictionary<string, string>? ViewState { get; init; }

    /// <summary>上报可恢复状态与上下文摘要（转发到 dock 状态 + 服务端快照）。</summary>
    public required Action<WorkbenchViewReport> Report { get; init; }

    /// <summary>请求关闭自己。</summary>
    public required Action Close { get; init; }

    /// <summary>
    /// 向宿主发 <c>workbench.&lt;id&gt;.&lt;action&gt;</c> 请求。
    /// <para>kind 由框架拼装（自动带本实例的 dock），工作台不拼字符串。</para>
    /// </summary>
    public required Func<string, JsonElement?, CancellationToken, Task<HostResponse>> CallHostAsync { get; init; }

    /// <summary>
    /// 声明本实例处理某个动作：<paramref name="action"/> 会变成 kind
    /// <c>workbench.&lt;id&gt;.&lt;action&gt;</c>，框架自动附上本实例的 dock 以便多实例消歧。
    /// <para>
    /// 处理器可能从后台线程调用；实现方负责自行编组到 UI 同步上下文（与旧文件面板一致）。
    /// 重复声明同一 action 会覆盖（重渲染是常态）。
    /// </para>
    /// </summary>
    public required Action<string, Func<HostRequest, CancellationToken, Task<HostResponse>>> RegisterAction { get; init; }

    /// <summary>撤销某个动作的声明（组件 Dispose 时调用；幂等）。</summary>
    public required Action<string> UnregisterAction { get; init; }
}
