using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms;

internal static class ChannelKeys
{
    public static string User(string channelId, string externalId) => channelId + "\u001f" + externalId;

    public static string Conversation(ConversationInfo info)
        => info.ChannelId + "\u001f" + info.Kind + "\u001f" + info.ExternalId;
}
