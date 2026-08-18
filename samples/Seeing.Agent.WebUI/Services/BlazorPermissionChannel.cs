using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using System.Collections.Concurrent;

namespace Seeing.Agent.WebUI.Services
{
    public class BlazorPermissionChannel : IPermissionChannel
    {
        private readonly EventStreamHandler _eventStreamHandler;
        private readonly State.SessionState _sessionState;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionChannelResult>> _pendingRequests = new();

        public int PendingCount => _pendingRequests.Count;

        public BlazorPermissionChannel(EventStreamHandler eventStreamHandler, State.SessionState sessionState)
        {
            _eventStreamHandler = eventStreamHandler;
            _sessionState = sessionState;
        }

        public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        {
            var requestId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<PermissionChannelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            ct.Register(() =>
            {
                if (_pendingRequests.TryRemove(requestId, out var t))
                    t.TrySetResult(PermissionChannelResult.Denied("操作已取消"));
            });

            await _eventStreamHandler.ProcessEventAsync(new PermissionRequestEvent
            {
                SessionId = request.SessionId ?? string.Empty,
                PermissionId = requestId,
                PermissionKind = request.PermissionKind,
                Resource = request.Resource,
                Arguments = request.Metadata,
                Message = BuildMessage(request.PermissionKind, request.Resource),
                RiskLevel = request.PermissionKind.Contains("write") ? "high" : "medium"
            });

            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);
            }
            catch (TimeoutException)
            {
                _pendingRequests.TryRemove(requestId, out _);
                return PermissionChannelResult.Denied("权限请求超时");
            }
        }

        public void RespondToPermission(string requestId, PermissionChannelAction action, string? resourceToRemember = null)
        {
            if (_pendingRequests.TryRemove(requestId, out var tcs))
            {
                var result = action switch
                {
                    PermissionChannelAction.Allow => PermissionChannelResult.Allowed(resourceToRemember),
                    PermissionChannelAction.Deny => PermissionChannelResult.Denied("用户拒绝", resourceToRemember),
                    _ => PermissionChannelResult.Denied("未知操作")
                };
                tcs.SetResult(result);
            }
        }

        private static string BuildMessage(string kind, string? resource) => kind switch
        {
            "tool.execute" => $"工具 {resource} 需要权限确认",
            "filesystem.write" => $"写入文件 {resource} 需要权限确认",
            "filesystem.read" => $"读取文件 {resource} 需要权限确认",
            "filesystem.external" => $"访问工作区外路径 {resource} 需要权限确认",
            "filesystem.workspace_extend" => $"请求将 {resource} 加入工作区白名单",
            "shell.execute" => $"执行命令 {resource} 需要权限确认",
            "network.fetch" => $"访问 URL {resource} 需要权限确认",
            "network.search" => $"搜索操作需要权限确认",
            _ => $"操作 {resource} 需要权限确认"
        };
    }
}
