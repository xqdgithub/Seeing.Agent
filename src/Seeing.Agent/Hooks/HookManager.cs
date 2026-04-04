using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using System.Collections.Concurrent;

namespace Seeing.Agent.Hooks
{
    /// <summary>
    /// Hook 管理器 - 管理生命周期钩子处理器的注册和触发
    /// </summary>
    public class HookManager : IHookManager
    {
        private readonly ILogger<HookManager> _logger;
        private readonly ConcurrentDictionary<string, List<IHookHandler>> _handlers = new();

        public HookManager(ILogger<HookManager> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 注册 Hook 处理器
        /// </summary>
        public void RegisterHandler(IHookHandler handler)
        {
            if (handler == null)
            {
                _logger.LogWarning("尝试注册空 Hook 处理器，已忽略");
                return;
            }

            var handlers = _handlers.GetOrAdd(handler.HookPoint, _ => new List<IHookHandler>());
            lock (handlers)
            {
                handlers.Add(handler);
                handlers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            }

            _logger.LogDebug("注册 Hook 处理器: HookPoint={HookPoint}, Priority={Priority}",
                handler.HookPoint, handler.Priority);
        }

        /// <summary>
        /// 触发 Hook（支持 input/output 模式，对齐 opencode 风格）
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <param name="input">输入数据（只读）</param>
        /// <param name="output">输出数据（可被 Hook 修改）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<HookResult> TriggerAsync(
            string hookPoint, 
            Dictionary<string, object>? input = null, 
            Dictionary<string, object>? output = null,
            CancellationToken cancellationToken = default)
        {
            if (!_handlers.TryGetValue(hookPoint, out var handlers) || handlers.Count == 0)
            {
                return new HookResult { Continue = true, ModifiedData = output };
            }

            var context = new HookContext
            {
                HookPoint = hookPoint,
                Data = input ?? new Dictionary<string, object>(),
                Output = output ?? new Dictionary<string, object>(),
                CancellationToken = cancellationToken
            };

            _logger.LogDebug("触发 Hook: {HookPoint}, Handlers={Count}", hookPoint, handlers.Count);

            foreach (var handler in handlers)
            {
                try
                {
                    var result = await handler.ExecuteAsync(context);
                    if (!result.Continue)
                    {
                        _logger.LogDebug("Hook 链被中断: HookPoint={HookPoint}, Handler={Handler}",
                            hookPoint, handler.GetType().Name);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hook 执行错误: HookPoint={HookPoint}, Handler={Handler}",
                        hookPoint, handler.GetType().Name);
                }
            }

            return new HookResult 
            { 
                Continue = true, 
                ModifiedData = output  // 返回可能被修改的 output
            };
        }

        /// <summary>
        /// 获取指定 Hook 点的处理器数量
        /// </summary>
        public int GetHandlerCount(string hookPoint)
        {
            return _handlers.TryGetValue(hookPoint, out var handlers) ? handlers.Count : 0;
        }

        /// <summary>
        /// 移除 Hook 处理器
        /// </summary>
        public bool RemoveHandler(string hookPoint, IHookHandler handler)
        {
            if (!_handlers.TryGetValue(hookPoint, out var handlers))
                return false;
            
            lock (handlers)
            {
                var removed = handlers.Remove(handler);
                if (removed)
                {
                    _logger.LogDebug("移除 Hook 处理器: HookPoint={HookPoint}, Handler={Handler}",
                        hookPoint, handler.GetType().Name);
                }
                return removed;
            }
        }

        /// <summary>
        /// 移除指定 Hook 点的所有处理器
        /// </summary>
        public bool ClearHandlers(string hookPoint)
        {
            var removed = _handlers.TryRemove(hookPoint, out var handlers);
            if (removed)
            {
                _logger.LogDebug("清除 Hook 点所有处理器: HookPoint={HookPoint}, Count={Count}",
                    hookPoint, handlers?.Count ?? 0);
            }
            return removed;
        }
    }
}