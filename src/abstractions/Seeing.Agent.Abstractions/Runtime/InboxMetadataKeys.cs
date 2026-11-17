namespace Seeing.Agent.Abstractions.Runtime;

public static class InboxMetadataKeys
{
    public const string Delivery = "inbox.delivery";
    public const string Consumed = "inbox.consumed";
    /// <summary>
    /// 临时搁置：仍算排队（UI/计数可见），但不进入本轮消费集合。
    /// 由 <c>InterruptThroughAsync</c> 打上，本批消费后清除。
    /// </summary>
    public const string Held = "inbox.held";
    public const string SenderKind = "inbox.sender_kind";
    public const string SenderId = "inbox.sender_id";
    public const string SenderName = "inbox.sender_name";
    public const string Presentation = "inbox.presentation";
    public const string Source = "inbox.source";
    public const string Kind = "inbox.kind";
    public const string AgentId = "inbox.agent_id";
    public const string ModelId = "inbox.model_id";
    /// <summary>ConversationInfo 的 camelCase JSON。</summary>
    public const string Conversation = "inbox.conversation";
    public const string ThinkingEffort = "inbox.thinking_effort";
    public const string ModeId = "inbox.mode_id";
    public const string WorkingDirectory = "inbox.working_dir";
}
