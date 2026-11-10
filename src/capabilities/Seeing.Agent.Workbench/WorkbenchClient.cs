using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Ui.Workbench;
using System.Text.Json;

namespace Seeing.Agent.Workbench;

/// <summary>
/// <see cref="IWorkbenchClient"/> 的唯一实现：拼 kind → 走宿主请求通道 → 归一化结果。
/// <para>
/// 归一化的价值在于<b>歧义判定</b>：同一工作台在左右两个 dock 各开一个实例时，
/// <c>workbench.&lt;id&gt;.&lt;action&gt;</c> 的 kind 完全相同，只有 <c>payload.dock</c> 能区分。
/// 调用方（插件工具）不必知道这条规则，也不该各自实现一遍。
/// </para>
/// </summary>
public sealed class WorkbenchClient : IWorkbenchClient
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WorkbenchClient> _logger;

    /// <summary>创建客户端。</summary>
    /// <param name="services">服务提供者（宿主请求管理器按需解析，避免无 UI 宿主下强依赖）。</param>
    /// <param name="logger">可选日志。</param>
    public WorkbenchClient(IServiceProvider services, ILogger<WorkbenchClient>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? NullLogger<WorkbenchClient>.Instance;
    }

    /// <inheritdoc />
    public async Task<WorkbenchCallResult> SendAsync(
        string sessionId,
        string workbenchId,
        string action,
        JsonElement? payload = null,
        WorkbenchDock? dock = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new WorkbenchCallResult(WorkbenchCallStatus.Failed, "缺少会话 id");

        if (string.Equals(workbenchId, WorkbenchRequestKinds.ReservedRegistryId, StringComparison.Ordinal))
            return new WorkbenchCallResult(WorkbenchCallStatus.Failed, "'registry' 是保留 id，不能作为工作台 id");

        var manager = _services.GetService(typeof(IHostRequestManager)) as IHostRequestManager;
        if (manager is null)
            return new WorkbenchCallResult(WorkbenchCallStatus.Unavailable, "宿主请求通道未接入");

        var surfaces = _services.GetService(typeof(IHostSurfaceRegistry)) as IHostSurfaceRegistry;
        if (surfaces is not null && !surfaces.CanSurface(sessionId))
        {
            return new WorkbenchCallResult(
                WorkbenchCallStatus.Unavailable,
                "界面未打开该工作台（当前没有可呈现该会话的窗口）");
        }

        var request = new HostRequest
        {
            SessionId = sessionId,
            Kind = WorkbenchRequestKinds.For(workbenchId, action),
            Payload = BuildPayload(payload, dock)
        };

        try
        {
            var ticket = await manager.BeginAsync(request, ct).ConfigureAwait(false);
            var response = await manager.WaitAsync(ticket, ct).ConfigureAwait(false);
            return Normalize(response, dock);
        }
        catch (OperationCanceledException)
        {
            return new WorkbenchCallResult(WorkbenchCallStatus.Cancelled, "工作台调用已取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作台调用失败：{WorkbenchId}/{Action}", workbenchId, action);
            return new WorkbenchCallResult(WorkbenchCallStatus.Failed, ex.Message);
        }
    }

    /// <summary>把 dock 合进载荷；载荷不是对象时用一个只含 dock 的对象替换。</summary>
    private static JsonElement? BuildPayload(JsonElement? payload, WorkbenchDock? dock)
    {
        if (dock is null)
            return payload;

        var dockText = dock.Value.ToString();
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["dock"] = JsonSerializer.SerializeToElement(dockText)
        };

        if (payload is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
                entries[property.Name] = property.Value;
        }

        return JsonSerializer.SerializeToElement(entries);
    }

    private static WorkbenchCallResult Normalize(HostResponse response, WorkbenchDock? dock)
    {
        var reason = response.Reason;

        return response.Status switch
        {
            HostResponseStatus.Completed => new WorkbenchCallResult(
                WorkbenchCallStatus.Completed,
                "已完成",
                response.Payload,
                response.RequestId),

            HostResponseStatus.Unavailable => new WorkbenchCallResult(
                WorkbenchCallStatus.Unavailable,
                reason ?? "界面未打开该工作台",
                null,
                response.RequestId),

            HostResponseStatus.Timeout => new WorkbenchCallResult(
                WorkbenchCallStatus.Timeout,
                "工作台未在限定时间内响应",
                null,
                response.RequestId),

            HostResponseStatus.Cancelled => new WorkbenchCallResult(
                WorkbenchCallStatus.Cancelled,
                "工作台调用已取消",
                null,
                response.RequestId),

            _ => new WorkbenchCallResult(
                WorkbenchCallStatus.Failed,
                reason ?? (dock is null
                    ? "工作台调用失败；若该工作台在左右两侧各开一个，请指定 dock"
                    : "工作台调用失败"),
                null,
                response.RequestId)
        };
    }
}
