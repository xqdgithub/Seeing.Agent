using Seeing.Agent.WebUI.Components.Messaging;

namespace Seeing.Agent.WebUI.Rendering.Providers;

/// <summary>
/// 问答槽位提供者：为每个工具调用内联渲染问答卡片槽位。
/// </summary>
/// <remarks>
/// 无状态实现；槽位组件按 <c>CallId</c> 关联在途问答请求（无 pending 时空渲染）。
/// </remarks>
public sealed class QuestionToolCallSlotProvider : IToolCallSlotProvider
{
    /// <inheritdoc />
    public int Order => 110;

    /// <inheritdoc />
    public Type? GetSlotComponentType(string callId) => typeof(ToolQuestionSlot);
}
