using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.Directories;

namespace Seeing.Agent.Comms.Tests.Directories;

public class JsonChannelDirectoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "comms-dir-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task Save_AtomicMove_ReloadsOptedOutAndMembers()
    {
        var options = Options.Create(new CommsOptions { DirectoryFilePath = _path });
        var store = new JsonChannelDirectoryStore(options);
        var service = new ChannelDirectoryService(store, options);
        var seen = DateTimeOffset.UtcNow;
        service.RegisterInbound(new ChannelInboundMessage
        {
            Sender = new ChannelUserIdentity { ChannelId = "qq", ExternalId = "u1", DisplayName = "Ada" },
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g1",
                Title = "group",
            },
            SessionId = "s",
            Text = "hi",
        });
        await service.SetOptOutAsync("qq", "u1", true);

        File.Exists(_path).Should().BeTrue();
        File.Exists(_path + ".tmp").Should().BeFalse();

        var reloaded = new ChannelDirectoryService(new JsonChannelDirectoryStore(options), options);
        var user = await reloaded.FindUserAsync("qq", "u1");
        user.Should().NotBeNull();
        user!.OptedOut.Should().BeTrue();
        user.DisplayName.Should().Be("Ada");
        user.LastSeenAt.Should().BeOnOrAfter(seen);

        var conversations = await reloaded.ListConversationsAsync("qq");
        conversations.Should().ContainSingle();
        conversations[0].Conversation.Kind.Should().Be(ConversationKind.Group);
        conversations[0].MemberExternalIds.Should().Contain("u1");

        reloaded.RegisterInbound(new ChannelInboundMessage
        {
            Sender = new ChannelUserIdentity { ChannelId = "qq", ExternalId = "u1" },
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g1",
            },
            SessionId = "s",
            Text = "again",
        });
        (await reloaded.FindUserAsync("qq", "u1"))!.OptedOut.Should().BeTrue();
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        if (File.Exists(_path + ".tmp"))
            File.Delete(_path + ".tmp");
    }
}
