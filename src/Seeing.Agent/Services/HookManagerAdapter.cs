using Seeing.Agent.Core.Hooks;

namespace Seeing.Agent.Services
{
    /// <summary>
    /// Hook 管理器适配器 - 将 Seeing.Agent.Core.Hooks.IHookManager 适配为 Seeing.Session.Hooks.IHookManager
    /// </summary>
    public class HookManagerAdapter : Seeing.Session.Hooks.IHookManager
    {
        private readonly IHookManager _inner;

        public HookManagerAdapter(IHookManager inner)
        {
            _inner = inner;
        }

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
