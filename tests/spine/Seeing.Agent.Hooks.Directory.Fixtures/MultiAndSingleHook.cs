using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Hooks.Directory.Fixtures;

public sealed class MultiAndSingleHook : IHookHandler, IMultiHookHandler
{
    public HookSpec Spec => HookRegistry.LlmParams;

    public IReadOnlyList<HookSpec> Specs { get; } = [HookRegistry.LlmParams];

    public int Priority => 10;

    public Task<HookResult> ExecuteAsync(HookPayload payload) => Task.FromResult(HookResult.Success);
}
