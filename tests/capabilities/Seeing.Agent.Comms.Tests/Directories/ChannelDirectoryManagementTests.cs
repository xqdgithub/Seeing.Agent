using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.Directories;
using Xunit;

namespace Seeing.Agent.Comms.Tests.Directories;

/// <summary>
/// 通联目录的人工管理：改显示名、删除、屏蔽/解封。
/// <para>重点锁住「删除 vs 屏蔽」的区别：删除只是移除当前条目，入站会重新登记；
/// 屏蔽必须持久拦住登记，否则管理员删掉的对话会被下一条消息立刻复活。</para>
/// </summary>
public class ChannelDirectoryManagementTests
{
    [Fact]
    public async Task RenameConversation_ShouldUpdateTitleOnly()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1", title: "旧名"));

        await directory.RenameConversationAsync("qq", ConversationKind.Group, "g1", "新名");

        var entry = await directory.FindConversationAsync("qq", ConversationKind.Group, "g1");
        entry!.Conversation.Title.Should().Be("新名");
        // 平台身份不受影响——改了就无法投递。
        entry.Conversation.ExternalId.Should().Be("g1");
        entry.Conversation.Kind.Should().Be(ConversationKind.Group);
    }

    [Fact]
    public async Task RenameConversation_BlankTitle_ClearsToNull()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1", title: "旧名"));

        await directory.RenameConversationAsync("qq", ConversationKind.Group, "g1", "   ");

        var entry = await directory.FindConversationAsync("qq", ConversationKind.Group, "g1");
        entry!.Conversation.Title.Should().BeNull();
    }

    /// <summary>
    /// 入站的 Title 是「非空才覆盖」：用户手填的名字不该被下一条不带标题的消息抹掉。
    /// </summary>
    [Fact]
    public void RegisterInbound_ShouldKeepManuallyRenamedTitle()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1", title: "旧名"));
        directory.RenameConversationAsync("qq", ConversationKind.Group, "g1", "我改的名字")
            .GetAwaiter().GetResult();

        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1", title: null));

        var entry = directory.FindConversationAsync("qq", ConversationKind.Group, "g1")
            .GetAwaiter().GetResult();
        entry!.Conversation.Title.Should().Be("我改的名字");
    }

    [Fact]
    public async Task DeleteConversation_RemovesEntryAndAnchor()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1", replyTo: "m1"));

        await directory.DeleteConversationAsync("qq", ConversationKind.Group, "g1");

        (await directory.FindConversationAsync("qq", ConversationKind.Group, "g1")).Should().BeNull();
        directory.ResolveReplyAnchor(Conversation("qq", ConversationKind.Group, "g1")).Should().BeNull();
    }

    /// <summary>删除是「弱删除」：对方再来消息就重新登记——这是与屏蔽的核心区别。</summary>
    [Fact]
    public async Task DeleteConversation_ThenInbound_ReRegisters()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1"));
        await directory.DeleteConversationAsync("qq", ConversationKind.Group, "g1");

        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1"));

        (await directory.FindConversationAsync("qq", ConversationKind.Group, "g1")).Should().NotBeNull();
    }

    [Fact]
    public async Task BlockConversation_RemovesEntryAndBlocksInbound()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1", title: "目标群"));

        await directory.BlockConversationAsync("qq", ConversationKind.Group, "g1", "不想收了");

        directory.IsBlocked("qq", ConversationKind.Group, "g1").Should().BeTrue();
        (await directory.FindConversationAsync("qq", ConversationKind.Group, "g1")).Should().BeNull();

        // 关键断言：屏蔽后入站不得让它复活，且必须由结果类型明确告知调用方丢弃消息。
        var rejected = directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1"));
        rejected.Accepted.Should().BeFalse("屏蔽后准入必须被拒，调用方才有依据丢弃该消息");
        rejected.Reason.Should().Be(InboundRejectionReason.Blocked);
        (await directory.FindConversationAsync("qq", ConversationKind.Group, "g1")).Should().BeNull(
            "屏蔽名单必须压住「收到消息即登记」");

        var blocked = await directory.ListBlockedConversationsAsync();
        blocked.Should().ContainSingle(b => b.ExternalId == "g1").Which.Title.Should().Be("目标群",
            "屏蔽前应保留显示名，便于回看屏蔽了谁");
    }

    /// <summary>
    /// 准入结果语义守门：未屏蔽时必须放行，否则调用方会把正常消息丢掉。
    /// </summary>
    [Fact]
    public void AdmitInbound_NotBlocked_IsAccepted()
    {
        var directory = NewDirectory();

        var admission = directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1"));

        admission.Accepted.Should().BeTrue();
        admission.Reason.Should().BeNull("放行时不应带拒绝原因");
    }

    [Fact]
    public async Task BlockConversation_IsIdempotent_AndKeepsOriginalBlockedAt()
    {
        var directory = NewDirectory();
        await directory.BlockConversationAsync("qq", ConversationKind.Group, "g1");
        var first = (await directory.ListBlockedConversationsAsync()).Single().BlockedAt;

        await directory.BlockConversationAsync("qq", ConversationKind.Group, "g1", "再封一次");

        var entries = await directory.ListBlockedConversationsAsync();
        entries.Should().ContainSingle();
        entries[0].BlockedAt.Should().Be(first, "重复屏蔽不应重置屏蔽时间");
    }

    [Fact]
    public async Task UnblockConversation_AllowsInboundAgain()
    {
        var directory = NewDirectory();
        await directory.BlockConversationAsync("qq", ConversationKind.Group, "g1");
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1"))
            .Accepted.Should().BeFalse();
        (await directory.FindConversationAsync("qq", ConversationKind.Group, "g1")).Should().BeNull();

        await directory.UnblockConversationAsync("qq", ConversationKind.Group, "g1");

        directory.IsBlocked("qq", ConversationKind.Group, "g1").Should().BeFalse();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1")).Accepted.Should().BeTrue();
        (await directory.FindConversationAsync("qq", ConversationKind.Group, "g1")).Should().NotBeNull();
    }

    [Fact]
    public async Task BlockedConversation_ShouldNotLeakToOtherConversations()
    {
        var directory = NewDirectory();
        await directory.BlockConversationAsync("qq", ConversationKind.Group, "g1");

        // 同渠道不同对话、同对话不同渠道都不受影响。
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g2"));
        directory.AdmitInbound(Inbound("wecom", ConversationKind.Group, "g1"));

        (await directory.FindConversationAsync("qq", ConversationKind.Group, "g2")).Should().NotBeNull();
        (await directory.FindConversationAsync("wecom", ConversationKind.Group, "g1")).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteUser_RemovesEntry_AndInboundReRegisters()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Direct, "u1"));

        await directory.DeleteUserAsync("qq", "u1");
        (await directory.FindUserAsync("qq", "u1")).Should().BeNull();

        directory.AdmitInbound(Inbound("qq", ConversationKind.Direct, "u1"));
        (await directory.FindUserAsync("qq", "u1")).Should().NotBeNull();
    }

    /// <summary>
    /// 屏蔽名单里的通道必须仍出现在通道列表里——否则某通道的对话全被屏蔽后，
    /// 它就从列表消失，用户再也进不到该通道去解封。
    /// </summary>
    [Fact]
    public async Task ListChannelIds_IncludesChannelsOnlyPresentInBlockedList()
    {
        var directory = NewDirectory();
        directory.AdmitInbound(Inbound("qq", ConversationKind.Group, "g1"));
        directory.AdmitInbound(Inbound("wecom", ConversationKind.Group, "w1"));

        await directory.BlockConversationAsync("wecom", ConversationKind.Group, "w1");

        var channelIds = await directory.ListChannelIdsAsync();
        channelIds.Should().BeEquivalentTo(["qq", "wecom"]);
    }

    /// <summary>屏蔽名单与目录走同一份 JSON，重启后必须还在（否则屏蔽形同虚设）。</summary>
    [Fact]
    public async Task BlockedList_ShouldSurviveReload()
    {
        var store = new MemoryDirectoryStore();
        var options = Options.Create(new CommsOptions());
        var first = new ChannelDirectoryService(store, options);
        await first.BlockConversationAsync("qq", ConversationKind.Group, "g1", "持久化测试");

        var reloaded = new ChannelDirectoryService(store, options);

        reloaded.IsBlocked("qq", ConversationKind.Group, "g1").Should().BeTrue();
        var blocked = await reloaded.ListBlockedConversationsAsync();
        blocked.Should().ContainSingle().Which.Reason.Should().Be("持久化测试");
    }

    private static ChannelDirectoryService NewDirectory()
        => new(new MemoryDirectoryStore(), Options.Create(new CommsOptions()));

    private static ChannelInboundMessage Inbound(
        string channelId,
        ConversationKind kind,
        string externalId,
        string? title = null,
        string? replyTo = null)
        => new()
        {
            Sender = new ChannelUserIdentity { ChannelId = channelId, ExternalId = "u1" },
            Conversation = new ConversationInfo
            {
                ChannelId = channelId,
                Kind = kind,
                ExternalId = externalId,
                Title = title
            },
            ReplyToMsgId = replyTo,
            SessionId = "s1",
            Text = "hi",
        };

    private static ConversationInfo Conversation(
        string channelId,
        ConversationKind kind,
        string externalId)
        => new()
        {
            ChannelId = channelId,
            Kind = kind,
            ExternalId = externalId,
        };
}
