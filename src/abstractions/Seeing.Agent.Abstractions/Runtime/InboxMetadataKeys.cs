namespace Seeing.Agent.Abstractions.Runtime;

public static class InboxMetadataKeys
{
    public const string Delivery = "inbox.delivery";
    public const string Consumed = "inbox.consumed";
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
