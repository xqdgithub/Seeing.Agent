using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;
namespace Seeing.Agent.Models;

/// <summary>
/// 聊天选项 - 可选的执行配置
/// </summary>
public record ChatOptions
{
    /// <summary>指定 Agent ID（可选，使用默认）</summary>
    public string? AgentId { get; init; }
    
    /// <summary>指定 Model ID（可选）</summary>
    public string? ModelId { get; init; }
    
    /// <summary>ACP Mode ID（可选）</summary>
    public string? ModeId { get; init; }
    
    /// <summary>工作目录（可选，覆盖会话默认）</summary>
    public string? WorkingDirectory { get; init; }
    
    /// <summary>并发通道 ID（Gateway 用于队列隔离）</summary>
    public string? ChannelId { get; init; }
    
    /// <summary>用户 ID（Gateway 用于审计）</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// 跳过持久化用户消息（synthetic 已注入，idle resume 时使用）。
    /// </summary>
    public bool SkipUserMessagePersist { get; init; }

    /// <summary>
    /// 跳过项目指令注入。
    /// </summary>
    public bool SkipInstructionInject { get; init; }

    /// <summary>
    /// 权限通道（可选，覆盖默认权限通道）
    /// <para>
    /// - WebUI 调用时传递 BlazorPermissionChannel（支持交互式确认）
    /// - Gateway/后台调用时不传递，使用 DenyAllPermissionChannel 或 AutoApproveInstance
    /// </para>
    /// </summary>
    public Seeing.Agent.Abstractions.Permissions.IPermissionChannel? PermissionChannel { get; init; }

    /// <summary>
    /// 会话级自动批准策略（默认跟随全局配置）。
    /// <para>优先于全局 <c>Permission.AutoApproveAll</c>；<see cref="SessionAutoApprove.Enabled"/> 强制自动批准，<see cref="SessionAutoApprove.Disabled"/> 强制交互式确认。</para>
    /// </summary>
    public SessionAutoApprove AutoApprove { get; init; } = SessionAutoApprove.FollowGlobal;
}
