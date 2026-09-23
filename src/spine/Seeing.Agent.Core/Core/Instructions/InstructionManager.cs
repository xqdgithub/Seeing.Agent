using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令管理器实现：委托内部发现器扫描指令文件，并按指纹差异向会话注入变更。
/// </summary>
public sealed class InstructionManager : IInstructionManager
{
    private readonly InstructionDiscovery _discovery;
    private readonly ILogger<InstructionManager> _logger;

    /// <summary>
    /// 构造指令管理器，创建配套的指令发现器。
    /// </summary>
    public InstructionManager(
        ILogger<InstructionManager> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _discovery = new InstructionDiscovery(
            loggerFactory.CreateLogger<InstructionDiscovery>());
    }

    /// <summary>
    /// 委托发现器从 cwd 向上扫描至 workspaceRoot 的指令文件。
    /// </summary>
    public Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default) =>
        _discovery.DiscoverAsync(cwd, workspaceRoot, ct);

    /// <summary>
    /// 对比会话指纹与当前发现的指令文件，有变更时以用户消息注入并更新指纹。
    /// </summary>
    public async Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var normalizedCwd = Path.GetFullPath(cwd);
        var discovered = await DiscoverAsync(normalizedCwd, workspaceRoot, ct);
        var snapshot = InstructionFingerprintStore.Load(session);
        var changed = InstructionFingerprintStore.Diff(snapshot, discovered);

        if (changed.Count == 0)
        {
            return new InstructionInjectResult
            {
                Injected = false,
                Reason = ProjectInstructions.Reasons.None
            };
        }

        var reason = snapshot.Files.Count == 0
            ? ProjectInstructions.Reasons.Initial
            : !InstructionFingerprintStore.PathComparer.Equals(snapshot.Cwd, normalizedCwd)
                ? ProjectInstructions.Reasons.CwdChange
                : ProjectInstructions.Reasons.ContentChange;

        session.AddMessage(
            ProjectInstructionsRenderer.CreateUserMessage(normalizedCwd, reason, changed));
        InstructionFingerprintStore.MergeAndSave(session, normalizedCwd, changed);

        _logger.LogDebug(
            "向会话 {SessionId} 注入了 {Count} 个项目指令文件，原因: {Reason}",
            session.Id,
            changed.Count,
            reason);

        return new InstructionInjectResult
        {
            Injected = true,
            Reason = reason,
            InjectedPaths = changed.Select(file => file.Path).ToArray()
        };
    }

    /// <summary>
    /// 从会话元数据读取指令文件指纹快照。
    /// </summary>
    public InstructionFingerprintSnapshot GetFingerprints(SessionData session) =>
        InstructionFingerprintStore.Load(session);
}
