using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Collections.Concurrent;
using System.Reflection;

namespace Seeing.Agent.WebUI.Services
{
    /// <summary>
    /// Blazor UI 侧权限通道实现
    /// 通过 EventStreamHandler 派发权限请求事件给 UI，UI 响应后通过 RespondToPermission 回填决策
    /// </summary>
    public class BlazorPermissionChannel : Seeing.Agent.Core.Interfaces.IPermissionChannel
    {
        private readonly EventStreamHandler _eventStreamHandler;
        private readonly Seeing.Agent.WebUI.State.SessionState _sessionState;

        // 待处理的权限请求集合：requestId -> TaskCompletionSource<PermissionDecision>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pendingRequests = new();

        /// <summary>待处理权限请求数量</summary>
        public int PendingCount => _pendingRequests.Count;

        public BlazorPermissionChannel(EventStreamHandler eventStreamHandler, Seeing.Agent.WebUI.State.SessionState sessionState)
        {
            _eventStreamHandler = eventStreamHandler ?? throw new ArgumentNullException(nameof(eventStreamHandler));
            _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        }

        /// <summary>
        /// 请求工具执行权限
        /// </summary>
        public async Task<PermissionDecision> RequestToolPermissionAsync(
            string toolName,
            object? arguments,
            AgentContext context)
        {
            var requestId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            // 派发权限请求事件给 UI
            await _eventStreamHandler.ProcessEventAsync(new PermissionRequestEvent
            {
                SessionId = context.SessionId,
                PermissionId = requestId,
                PermissionKind = "tool",
                Resource = toolName,
                Arguments = arguments,
                Message = $"工具 {toolName} 需要权限确认",
                RiskLevel = "medium"
            });

            try
            {
                // 等待 UI 响应，5 分钟超时以避免无限等待
                return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5));
            }
            catch (TimeoutException)
            {
                _pendingRequests.TryRemove(requestId, out _);
                return PermissionDecision.Deny("权限请求超时");
            }
        }

        /// <summary>
        /// UI 调用：响应权限请求
        /// </summary>
        public void RespondToPermission(string requestId, PermissionAction action)
        {
            if (_pendingRequests.TryGetValue(requestId, out var tcs))
            {
                var decision = action switch
                {
                    PermissionAction.Allow => PermissionDecision.Allow(),
                    PermissionAction.Deny => PermissionDecision.Deny("用户拒绝"),
                    PermissionAction.Ask => PermissionDecision.Ask("需要用户进一步确认"),
                    _ => PermissionDecision.Deny("未知操作")
                };
                tcs.SetResult(decision);
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        /// <summary>
        /// 委托：请求确认，转发到工具权限请求以保持单一入口
        /// </summary>
        public Task<bool> RequestConfirmationAsync(PermissionRequest request)
        {
            // 将通用确认请求转发给工具权限请求的路径，使用一个虚拟工具名称进行描述
            var task = RequestToolPermissionAsync("permission_confirmation", request, new AgentContext());
            // 尝试从 PermissionDecision 对象反射获取布尔结果（Granted/Allowed/IsAllowed）
            return task.ContinueWith<bool>(t =>
            {
                var dec = t.Result;
                if (dec == null) return false;
                var tType = dec.GetType();
                foreach (var propName in new[] { "Granted", "Allowed", "IsAllowed" })
                {
                    var prop = tType.GetProperty(propName);
                    if (prop != null)
                    {
                        var value = prop.GetValue(dec);
                        if (value is bool b)
                        {
                            return b;
                        }
                    }
                }
                return false;
            });
        }

        /// <summary>
        /// 请求子代理调用权限（同样走权限通道，UI 将进行人机交互）
        /// </summary>
        public Task<PermissionDecision> RequestSubAgentPermissionAsync(
            string agentName,
            string prompt,
            AgentContext context)
        {
            return RequestToolPermissionAsync($"subagent:{agentName}", prompt, context);
        }

        /// <summary>
        /// 请求文件写入权限
        /// </summary>
        public Task<PermissionDecision> RequestWritePermissionAsync(
            string filePath,
            string? contentPreview,
            AgentContext context)
        {
            return RequestToolPermissionAsync($"write:{filePath}", contentPreview, context);
        }
    }
}
