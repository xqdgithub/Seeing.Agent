using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Permissions;

public sealed record PermissionRequest
{
    /// <summary>由 Manager 分配；调用方可留空。</summary>
    public string? RequestId { get; init; }

    public required string SessionId { get; init; }
    public string? CallId { get; init; }          // 工具调用 id：内联卡片关联键（不用于跨门合并）
    public string? LoopId { get; init; }
    public string? AgentName { get; init; }
    public required string PermissionKind { get; init; }
    public string? Resource { get; init; }
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

    /// <summary>展示参数（Arguments 供 UI/客户端展示；Metadata 供策略与记忆）。</summary>
    public object? Arguments { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    public string RiskLevel { get; init; } = "medium";
    public string? Message { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>执行级冻结覆盖（来自 ChatOptions.AutoApprove）；null=FollowGlobal。</summary>
    public SessionAutoApprove? Override { get; init; }

    /// <summary>强制交互：跳过规则/开关的免询问放行分支（ACP 声明）。</summary>
    public bool RequireInteraction { get; init; }

    /// <summary>允许的动作集合（呈现用）。由执行级授权器按 kind 注入（§5.1）；此处为缺省。</summary>
    public IReadOnlyList<PermissionGrantScope> AllowedScopes { get; init; } =
        [PermissionGrantScope.Once, PermissionGrantScope.Session];
}

public enum PermissionGrantScope { Once = 0, Session = 1, SessionDirectory = 2 }

public enum PermissionResolvedBy { User = 0, Policy = 1, Cancellation = 2, Timeout = 3, NoChannel = 4 }
