using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Hosting.Web.Permissions;

/// <summary>
/// 将权限请求事件推送到 UI（通常由 sample 的 EventStreamHandler 实现）。
/// </summary>
public interface IPermissionEventSink
{
    Task PublishAsync(PermissionRequestEvent evt, CancellationToken ct = default);
}
