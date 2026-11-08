using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms;

internal static class ChannelKeys
{
    public static string User(string channelId, string externalId) => channelId + "\u001f" + externalId;

    public static string Conversation(ConversationInfo info)
        => info.ChannelId + "\u001f" + info.Kind + "\u001f" + info.ExternalId;

    /// <summary>对话身份键（渠道 + 类型 + 外部 id）；屏蔽名单与目录共用同一套键规则。</summary>
    public static string Conversation(string channelId, ConversationKind kind, string externalId)
        => channelId + "\u001f" + kind + "\u001f" + externalId;
}
