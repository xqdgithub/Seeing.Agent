using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Hooks.Directory.Fixtures;

public sealed class ThrowingHook : IHookHandler
{
    public HookSpec Spec => HookRegistry.ToolExecuteBefore;

    public int Priority => 40;

    public Task<HookResult> ExecuteAsync(HookPayload payload) =>
        throw new InvalidOperationException("dll hook failed");
}
