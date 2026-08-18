using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Tools.BuiltIn.FileSystem;

using Seeing.Agent.Abstractions.Permissions;
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限通道串行包装 + 会话级记忆 + 工作区边界检查：
/// 1. filesystem.* 操作若路径在工作区内 → 自动通过
/// 2. 查记忆 → 命中直接返回
/// 3. 未命中 → 获取信号量 → 询问内部通道 → 释放信号量
/// 4. 若用户选择"会话内记住" → 写入记忆
/// </summary>
public sealed class SerializingPermissionChannel : IPermissionChannel
{
    private readonly IPermissionChannel _inner;
    private readonly IPermissionMemory _memory;
    private readonly IWorkspaceProvider? _workspace;
    private readonly IWorkspaceWhitelist? _whitelist;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SerializingPermissionChannel(
        IPermissionChannel inner,
        IPermissionMemory memory,
        IWorkspaceProvider? workspace = null,
        IWorkspaceWhitelist? whitelist = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _workspace = workspace;
        _whitelist = whitelist;
    }

    public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        // 工作区边界检查：filesystem.* 操作若路径在工作区内或白名单内则自动通过
        if (request.Resource != null &&
            request.PermissionKind.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase))
        {
            var inWorkspace = _workspace != null &&
                FileSystemHelper.IsPathWithinDirectory(request.Resource, _workspace.WorkspaceRoot);
            var inWhitelist = _whitelist != null &&
                _whitelist.Contains(request.SessionId ?? string.Empty, request.Resource);
            if (inWorkspace || inWhitelist)
                return PermissionChannelResult.Allowed();
        }

        // 检查会话记忆（无需串行化，记忆命中直接返回）
        if (!string.IsNullOrEmpty(request.SessionId))
        {
            var memoryHit = _memory.Match(request.PermissionKind, request.Resource, request.SessionId);
            if (memoryHit != null)
            {
                return memoryHit.Action == PermissionMemoryAction.Allow
                    ? PermissionChannelResult.Allowed()
                    : PermissionChannelResult.Denied("会话记忆拒绝");
            }
        }

        // 记忆未命中，串行化询问用户
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _inner.RequestAsync(request, ct).ConfigureAwait(false);

            // 若用户选择了"记住"，写入记忆
            if (!string.IsNullOrEmpty(result.ResourceToRemember) && !string.IsNullOrEmpty(request.SessionId))
            {
                _memory.Remember(request.SessionId, new PermissionMemoryEntry
                {
                    PermissionKind = request.PermissionKind,
                    Resource = result.ResourceToRemember,
                    Action = result.Action == PermissionChannelAction.Allow
                        ? PermissionMemoryAction.Allow
                        : PermissionMemoryAction.Deny
                });

                result = new PermissionChannelResult
                {
                    Action = result.Action,
                    Reason = result.Reason
                };
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
