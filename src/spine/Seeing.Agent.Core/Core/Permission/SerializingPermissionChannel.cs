using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限通道串行包装 + 会话级记忆 + 工作区边界检查：
/// 1. Restrict=true 且 filesystem.* 且无 SessionId → Deny（不 Ask）
/// 2. filesystem.* 在工作区/白名单内 → 自动 Allow（Restrict 时写 whitelist）
/// 3. 查记忆 → 命中直接返回（Restrict∧Allow 时写 whitelist）
/// 4. 未命中 → 串行询问内部通道；Restrict∧Allow 时写 whitelist
/// </summary>
public sealed class SerializingPermissionChannel : IPermissionChannel
{
    private readonly IPermissionChannel _inner;
    private readonly IPermissionMemory _memory;
    private readonly IWorkspaceProvider? _workspace;
    private readonly IWorkspaceWhitelist? _whitelist;
    private readonly IOptionsMonitor<SeeingAgentOptions>? _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SerializingPermissionChannel(
        IPermissionChannel inner,
        IPermissionMemory memory,
        IWorkspaceProvider? workspace = null,
        IWorkspaceWhitelist? whitelist = null,
        IOptionsMonitor<SeeingAgentOptions>? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _workspace = workspace;
        _whitelist = whitelist;
        _options = options;
    }

    public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        var isFilesystem = request.PermissionKind.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase);
        var restrict = IsRestricting();

        if (restrict && isFilesystem && string.IsNullOrEmpty(request.SessionId))
        {
            return PermissionChannelResult.Denied("缺少会话 ID，无法在硬边界模式下访问文件");
        }

        // 工作区边界检查：filesystem.* 操作若路径在工作区内或白名单内则自动通过
        if (request.Resource != null && isFilesystem)
        {
            var inWorkspace = _workspace != null &&
                PathSafety.IsPathWithinDirectory(request.Resource, _workspace.GetProjectRoot());
            var inWhitelist = _whitelist != null &&
                _whitelist.Contains(request.SessionId ?? string.Empty, request.Resource);
            if (inWorkspace || inWhitelist)
            {
                ExpandWhitelistOnAllow(request);
                return PermissionChannelResult.Allowed();
            }
        }

        // 检查会话记忆（无需串行化，记忆命中直接返回）
        if (!string.IsNullOrEmpty(request.SessionId))
        {
            var memoryHit = _memory.Match(request.PermissionKind, request.Resource, request.SessionId);
            if (memoryHit != null)
            {
                if (memoryHit.Action == PermissionMemoryAction.Allow)
                {
                    ExpandWhitelistOnAllow(request);
                    return PermissionChannelResult.Allowed();
                }

                return PermissionChannelResult.Denied("会话记忆拒绝");
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

            if (result.Action == PermissionChannelAction.Allow)
                ExpandWhitelistOnAllow(request);

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsRestricting() =>
        _options?.CurrentValue.Workspace.RestrictToWorkspace == true;

    private void ExpandWhitelistOnAllow(PermissionRequest request)
    {
        if (!IsRestricting()) return;
        if (_whitelist == null) return;
        if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrWhiteSpace(request.Resource)) return;
        if (!request.PermissionKind.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var full = Path.GetFullPath(request.Resource);
            var dir = Directory.Exists(full)
                ? full
                : (Path.GetDirectoryName(full) ?? full);
            if (!string.IsNullOrWhiteSpace(dir))
                _whitelist.Add(request.SessionId, dir);
        }
        catch
        {
            // 扩权失败不阻断 Allow；门闸仍可能防御性拒绝
        }
    }
}
