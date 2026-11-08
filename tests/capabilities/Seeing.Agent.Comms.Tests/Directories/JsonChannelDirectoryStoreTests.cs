using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Comms.Directories;
using Seeing.Agent.Configuration;
using Xunit;

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
        service.AdmitInbound(new ChannelInboundMessage
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

        var conversations = await reloaded.ListConversationsAsync("qq", 0, 50);
        conversations.Should().ContainSingle();
        conversations[0].Conversation.Kind.Should().Be(ConversationKind.Group);
        conversations[0].MemberExternalIds.Should().Contain("u1");

        reloaded.AdmitInbound(new ChannelInboundMessage
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

    /// <summary>
    /// 目录文件由多进程共享（WebUI / GatewayServer / ChannelHost 桥）。外部进程写入后，
    /// 本实例必须能读到——否则表现为「定时任务能选到出站会话，目录页却是空的」。
    /// </summary>
    [Fact]
    public async Task ExternalWrite_IsVisibleAfterReload()
    {
        var options = Options.Create(new CommsOptions { DirectoryFilePath = _path });
        var reader = new JsonChannelDirectoryStore(options);

        // 初始为空：确认前提成立。
        (await reader.ListConversationsAsync("qq", 0, 50)).Should().BeEmpty();

        // 另一个进程（这里用独立 store 实例模拟）写入并落盘。
        var writer = new JsonChannelDirectoryStore(options);
        await writer.SaveConversationAsync(new ConversationEntry
        {
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g-other-process",
                Title = "别的进程写的"
            },
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            MemberExternalIds = ["u1"]
        });

        // reader 从未重启，但下一次读必须看见对方写入的内容。
        var conversations = await reader.ListConversationsAsync("qq", 0, 50);
        conversations.Should().ContainSingle(c => c.Conversation.ExternalId == "g-other-process");
    }

    /// <summary>
    /// 写前先合并磁盘：本进程保存时不得覆盖掉其它进程已写入的条目
    /// （每个进程各持全量快照，直接覆盖会互相丢数据）。
    /// </summary>
    [Fact]
    public async Task Save_DoesNotClobberOtherProcessEntries()
    {
        var options = Options.Create(new CommsOptions { DirectoryFilePath = _path });
        var first = new JsonChannelDirectoryStore(options);
        var second = new JsonChannelDirectoryStore(options);

        // second 先写一条，此时 first 的内存还不知道它。
        await second.SaveConversationAsync(Conversation("qq", "g-second"));
        // first 再写自己的（写前应把磁盘上的 g-second 合并进来）。
        await first.SaveConversationAsync(Conversation("qq", "g-first"));

        var reloaded = new JsonChannelDirectoryStore(options);
        var ids = (await reloaded.ListConversationsAsync("qq", 0, 50))
            .Select(c => c.Conversation.ExternalId)
            .ToList();
        ids.Should().BeEquivalentTo(["g-first", "g-second"]);
    }

    private static ConversationEntry Conversation(string channelId, string externalId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationEntry
        {
            Conversation = new ConversationInfo
            {
                ChannelId = channelId,
                Kind = ConversationKind.Group,
                ExternalId = externalId
            },
            FirstSeenAt = now,
            LastSeenAt = now,
            MemberExternalIds = ["u1"]
        };
    }

    // ===== 路径解析 =====

    /// <summary>
    /// 默认落在项目级：目录是「这个工作区收到过哪些渠道对话」的事实，随工作区走才不会串场
    /// （同一台机器多个项目时，用户级单一路径会把不同项目的对话混在一起）。
    /// </summary>
    [Fact]
    public void ResolveDirectoryPath_ProjectWorkspace_UsesProjectSeeingDirectory()
    {
        var options = new CommsOptions();
        var directories = new FakeDirectories(@"C:\repo\proj\.seeing");

        var path = JsonChannelDirectoryStore.ResolveDirectoryPath(options, directories);

        path.Should().Be(Path.Combine(@"C:\repo\proj\.seeing", "comms", "directory.json"));
    }

    /// <summary>显式配置永远优先于推导，两者都不看。</summary>
    [Fact]
    public void ResolveDirectoryPath_ExplicitPath_Wins()
    {
        var options = new CommsOptions { DirectoryFilePath = @"D:\custom\dir.json" };
        var directories = new FakeDirectories(@"C:\repo\proj\.seeing");

        JsonChannelDirectoryStore.ResolveDirectoryPath(options, directories)
            .Should().Be(@"D:\custom\dir.json");
    }

    /// <summary>
    /// 工作区未选定（占位目录）时回退用户级：此时没有项目可归属，
    /// 但桥进程仍可能在运行并需要落盘。
    /// </summary>
    [Fact]
    public void ResolveDirectoryPath_UnresolvedWorkspace_FallsBackToUser()
    {
        var options = new CommsOptions();
        var directories = new FakeDirectories(WorkspaceProviderPlaceholder.ProjectSeeingDirectory);

        var path = JsonChannelDirectoryStore.ResolveDirectoryPath(options, directories);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing", "comms", "directory.json");
        path.Should().Be(expected);
    }

    /// <summary>没有目录提供者时（旧调用方 / 单测）同样回退用户级，不抛异常。</summary>
    [Fact]
    public void ResolveDirectoryPath_NoDirectories_FallsBackToUser()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing", "comms", "directory.json");

        JsonChannelDirectoryStore.ResolveDirectoryPath(new CommsOptions()).Should().Be(expected);
        JsonChannelDirectoryStore.ResolveDirectoryPath(new CommsOptions(), null).Should().Be(expected);
    }

    private sealed class FakeDirectories(string projectSeeingDirectory) : ISeeingDirectories
    {
        public string UserSeeingDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

        public string ProjectSeeingDirectory { get; } = projectSeeingDirectory;
    }

    /// <summary>
    /// <c>ProjectSeeingDirectory</c> 在工作区未选定时按契约会<b>抛异常</b>。
    /// 路径解析不能让这个异常冒泡到 Store 构造函数——否则 <c>IChannelDirectory</c> 整个服务
    /// 都解析不出来，页面表现为「目录永远是空的」。
    /// </summary>
    [Fact]
    public void ResolveDirectoryPath_ProjectProvidesThrows_FallsBackToUser()
    {
        var options = new CommsOptions();
        var directories = new ThrowingDirectories();

        var path = JsonChannelDirectoryStore.ResolveDirectoryPath(options, directories);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing", "comms", "directory.json");
        path.Should().Be(expected);
    }

    /// <summary>构造 Store 本身也不能因为工作区未选定而抛异常。</summary>
    [Fact]
    public void Constructor_ProjectProvidesThrows_DoesNotThrow()
    {
        var options = Options.Create(new CommsOptions { DirectoryFilePath = _path });

        var act = () => new JsonChannelDirectoryStore(options, directories: new ThrowingDirectories());

        act.Should().NotThrow();
    }

    private sealed class ThrowingDirectories : ISeeingDirectories
    {
        public string UserSeeingDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

        public string ProjectSeeingDirectory =>
            throw new InvalidOperationException("工作区尚未选定");
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        if (File.Exists(_path + ".tmp"))
            File.Delete(_path + ".tmp");
    }
}
