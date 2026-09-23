using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Core.Services
{
    /// <summary>
    /// Hook 管理器适配器 - 将 Seeing.Agent.Abstractions.Hooks.IHookManager 适配为 Seeing.Session.Hooks.IHookManager
    /// </summary>
    public class HookManagerAdapter : Seeing.Session.Hooks.IHookManager
    {
        private readonly IHookManager _inner;

        /// <summary>初始化 Hook 管理器适配器，包装内部 IHookManager。</summary>
        public HookManagerAdapter(IHookManager inner)
        {
            _inner = inner;
        }

        /// <summary>以 Fire-and-Forget 策略触发指定 Hook 点。</summary>
        public void TriggerFireAndForget(
            string hookPoint,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            IReadOnlyDictionary<string, object?>? result = null)
        {
            var spec = new HookSpec(HookPolicy.FireAndForget, hookPoint);
            _inner.TriggerFireAndForget(spec, sessionId, input, result);
        }
    }
}
