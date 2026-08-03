using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

public interface IInstructionManager
{
    Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default);

    Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default);

    InstructionFingerprintSnapshot GetFingerprints(SessionData session);
}
