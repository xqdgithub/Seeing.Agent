using System.Text.Json;
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Channels;

public static class ConversationInfoExtensions
{
    public static SessionChannelRoute ToSessionRoute(this ConversationInfo conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new SessionChannelRoute
        {
            ChannelId = conversation.ChannelId,
            Kind = JsonNamingPolicy.CamelCase.ConvertName(conversation.Kind.ToString()),
            ExternalId = conversation.ExternalId,
            Title = conversation.Title
        };
    }
}
