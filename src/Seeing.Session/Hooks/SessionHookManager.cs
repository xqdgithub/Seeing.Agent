using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Session.Hooks
{
    /// <summary>
    /// Session Hook 管理器 - 管理生命周期钩子处理器的注册和触发
    /// </summary>
    public class SessionHookManager
    {
        private readonly ILogger<SessionHookManager>? _logger;
        private readonly ConcurrentDictionary<string, List<ISessionHook>> _hooks = new();

        /// <summary>
        /// 创建 SessionHookManager 实例
        /// </summary>
        public SessionHookManager() { }

        /// <summary>
        /// 创建 SessionHookManager 实例（带日志）
        /// </summary>
        public SessionHookManager(ILogger<SessionHookManager>? logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 添加 Hook 处理器
        /// </summary>
        /// <param name="hook">Hook 处理器</param>
        public void AddHook(ISessionHook hook)
        {
            if (hook == null)
            {
                _logger?.LogWarning("尝试添加空 Hook 处理器，已忽略");
                return;
            }

            var hooks = _hooks.GetOrAdd(hook.HookPoint, _ => new List<ISessionHook>());
            lock (hooks)
            {
                hooks.Add(hook);
                hooks.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            }

            _logger?.LogDebug("添加 Session Hook: HookPoint={HookPoint}, Priority={Priority}",
                hook.HookPoint, hook.Priority);
        }

        /// <summary>
        /// 移除 Hook 处理器
        /// </summary>
        /// <param name="hook">Hook 处理器</param>
        /// <returns>是否成功移除</returns>
        public bool RemoveHook(ISessionHook hook)
        {
            if (hook == null || !_hooks.TryGetValue(hook.HookPoint, out var hooks))
                return false;

            lock (hooks)
            {
                var removed = hooks.Remove(hook);
                if (removed)
                {
                    _logger?.LogDebug("移除 Session Hook: HookPoint={HookPoint}", hook.HookPoint);
                }
                return removed;
            }
        }

        /// <summary>
        /// 移除指定 Hook 点的所有处理器
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <returns>是否成功移除</returns>
        public bool ClearHooks(string hookPoint)
        {
            var removed = _hooks.TryRemove(hookPoint, out var hooks);
            if (removed)
            {
                _logger?.LogDebug("清除 Hook 点所有处理器: HookPoint={HookPoint}, Count={Count}",
                    hookPoint, hooks?.Count ?? 0);
            }
            return removed;
        }

        /// <summary>
        /// 获取指定 Hook 点的处理器数量
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <returns>处理器数量</returns>
        public int GetHookCount(string hookPoint)
        {
            return _hooks.TryGetValue(hookPoint, out var hooks) ? hooks.Count : 0;
        }

        /// <summary>
        /// 触发 Hook（异步，不阻塞主流程）
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <param name="session">Session 数据</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task TriggerAsync(string hookPoint, SessionData? session = null, CancellationToken cancellationToken = default)
        {
            if (!_hooks.TryGetValue(hookPoint, out var hooks) || hooks.Count == 0)
                return;

            var context = new SessionHookContext
            {
                HookPoint = hookPoint,
                Session = session,
                SessionId = session?.Id,
                CancellationToken = cancellationToken
            };

            _logger?.LogDebug("触发 Session Hook: {HookPoint}, Hooks={Count}", hookPoint, hooks.Count);

            // 异步触发，不阻塞主流程
            _ = Task.Run(async () =>
            {
                foreach (var hook in hooks.ToList())
                {
                    try
                    {
                        var result = await hook.ExecuteAsync(context);
                        if (!result.Continue)
                        {
                            _logger?.LogDebug("Session Hook 链被中断: HookPoint={HookPoint}, Hook={Hook}",
                                hookPoint, hook.GetType().Name);
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _logger?.LogError(ex, "Session Hook 执行错误: HookPoint={HookPoint}, Hook={Hook}",
                            hookPoint, hook.GetType().Name);
                    }
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 触发 Hook（使用 Session ID）
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <param name="sessionId">Session ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task TriggerAsync(string hookPoint, string sessionId, CancellationToken cancellationToken = default)
        {
            if (!_hooks.TryGetValue(hookPoint, out var hooks) || hooks.Count == 0)
                return;

            var context = new SessionHookContext
            {
                HookPoint = hookPoint,
                SessionId = sessionId,
                CancellationToken = cancellationToken
            };

            _logger?.LogDebug("触发 Session Hook: {HookPoint}, SessionId={SessionId}, Hooks={Count}", 
                hookPoint, sessionId, hooks.Count);

            // 异步触发，不阻塞主流程
            _ = Task.Run(async () =>
            {
                foreach (var hook in hooks.ToList())
                {
                    try
                    {
                        var result = await hook.ExecuteAsync(context);
                        if (!result.Continue)
                        {
                            _logger?.LogDebug("Session Hook 链被中断: HookPoint={HookPoint}, Hook={Hook}",
                                hookPoint, hook.GetType().Name);
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _logger?.LogError(ex, "Session Hook 执行错误: HookPoint={HookPoint}, Hook={Hook}",
                            hookPoint, hook.GetType().Name);
                    }
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 触发 Hook（带额外数据）
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <param name="session">Session 数据</param>
        /// <param name="data">额外数据</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task TriggerAsync(string hookPoint, SessionData? session, Dictionary<string, object>? data, CancellationToken cancellationToken = default)
        {
            if (!_hooks.TryGetValue(hookPoint, out var hooks) || hooks.Count == 0)
                return;

            var context = new SessionHookContext
            {
                HookPoint = hookPoint,
                Session = session,
                SessionId = session?.Id,
                Data = data ?? new Dictionary<string, object>(),
                CancellationToken = cancellationToken
            };

            _logger?.LogDebug("触发 Session Hook: {HookPoint}, Hooks={Count}", hookPoint, hooks.Count);

            // 异步触发，不阻塞主流程
            _ = Task.Run(async () =>
            {
                foreach (var hook in hooks.ToList())
                {
                    try
                    {
                        var result = await hook.ExecuteAsync(context);
                        if (!result.Continue)
                        {
                            _logger?.LogDebug("Session Hook 链被中断: HookPoint={HookPoint}, Hook={Hook}",
                                hookPoint, hook.GetType().Name);
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _logger?.LogError(ex, "Session Hook 执行错误: HookPoint={HookPoint}, Hook={Hook}",
                            hookPoint, hook.GetType().Name);
                    }
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 触发 Hook（使用 sessionId 和额外数据）- 支持任意会话类型
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <param name="sessionId">Session ID</param>
        /// <param name="data">额外数据</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task TriggerWithDataAsync(string hookPoint, string sessionId, Dictionary<string, object>? data, CancellationToken cancellationToken = default)
        {
            if (!_hooks.TryGetValue(hookPoint, out var hooks) || hooks.Count == 0)
                return;

            var context = new SessionHookContext
            {
                HookPoint = hookPoint,
                SessionId = sessionId,
                Data = data ?? new Dictionary<string, object>(),
                CancellationToken = cancellationToken
            };

            _logger?.LogDebug("触发 Session Hook: {HookPoint}, SessionId={SessionId}, Hooks={Count}", 
                hookPoint, sessionId, hooks.Count);

            // 异步触发，不阻塞主流程
            _ = Task.Run(async () =>
            {
                foreach (var hook in hooks.ToList())
                {
                    try
                    {
                        var result = await hook.ExecuteAsync(context);
                        if (!result.Continue)
                        {
                            _logger?.LogDebug("Session Hook 链被中断: HookPoint={HookPoint}, Hook={Hook}",
                                hookPoint, hook.GetType().Name);
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _logger?.LogError(ex, "Session Hook 执行错误: HookPoint={HookPoint}, Hook={Hook}",
                            hookPoint, hook.GetType().Name);
                    }
                }
            }, cancellationToken);
        }
    }
}