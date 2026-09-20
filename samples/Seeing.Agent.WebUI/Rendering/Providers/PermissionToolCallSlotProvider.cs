using Seeing.Agent.WebUI.Components.Messaging;

namespace Seeing.Agent.WebUI.Rendering.Providers;

/// <summary>
/// 权限槽位提供者：为每个工具调用内联渲染权限审批卡片槽位。
/// </summary>
/// <remarks>
/// 无状态实现；槽位组件按 <c>CallId</c> 关联在途审批卡片。
/// </remarks>
public sealed class PermissionToolCallSlotProvider : IToolCallSlotProvider
{
    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public Type? GetSlotComponentType(string callId) => typeof(ToolPermissionSlot);
}
