using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Comms.Directories;
using Seeing.Agent.Comms.Extensions;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Comms.Tests.Extensions;

/// <summary>
/// 工作区切换参与者：<c>AddSeeingComms</c> 必须把 <see cref="CommsWorkspaceParticipant"/>
/// 登记进 <see cref="IWorkspaceApplyParticipant"/> 枚举（否则切换工作区后目录不会重载），
/// 并且它的 <c>OnAppliedAsync</c> 真的把单例 store 换到新工作区的目录。
/// </summary>
public class CommsWorkspaceParticipantTests : IDisposable
{
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "comms-ws-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "comms-ws-b-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddSeeingComms_RegistersWorkspaceParticipant()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // AddSeeingComms 把 CommsOptions 桥到 IConfigSectionStore：给它一个返回默认选项的替身。
        var configStore = new Mock<IConfigSectionStore>();
        configStore
            .Setup(s => s.GetSection<CommsOptions>(It.IsAny<string>()))
            .Returns(new CommsOptions());
        services.AddSingleton(configStore.Object);

        // 未选定工作区时目录路径回退用户级，这里给一个空的目录提供者即可。
        services.AddSingleton(Mock.Of<ISeeingDirectories>());

        services.AddSeeingComms(Mock.Of<IConfigSectionRegistry>());

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IWorkspaceApplyParticipant>()
            .Should().ContainSingle(p => p is CommsWorkspaceParticipant,
                "切换工作区后目录路径会变，必须有人显式重置单例里的工作区级状态");

        // 参与者靠枚举这个端口找到状态持有者：漏注册等于参与者什么也不做。
        provider.GetServices<ICommsWorkspaceScopedState>()
            .Select(s => s.GetType())
            .Should().BeEquivalentTo([typeof(JsonChannelDirectoryStore), typeof(ChannelDirectoryService)]);
    }

    [Fact]
    public async Task OnApplied_AfterWorkspaceSwitch_StoreServesNewWorkspaceDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_rootA, ".seeing", "comms"));
        Directory.CreateDirectory(Path.Combine(_rootB, ".seeing", "comms"));

        var directories = new MutableDirectories(Path.Combine(_rootA, ".seeing"));
        var options = Options.Create(new CommsOptions());

        // 工作区 A 的目录里有一条对话，工作区 B 的目录里是另一条。
        var storeA = new JsonChannelDirectoryStore(options, directories: directories);
        await storeA.SaveConversationAsync(Conversation("g-in-A"));

        directories.ProjectSeeingDirectory = Path.Combine(_rootB, ".seeing");
        var storeB = new JsonChannelDirectoryStore(options, directories: directories);
        await storeB.SaveConversationAsync(Conversation("g-in-B"));

        // 单例 store 此时仍指向 A：模拟切换前的状态。
        directories.ProjectSeeingDirectory = Path.Combine(_rootA, ".seeing");
        var store = new JsonChannelDirectoryStore(options, directories: directories);
        (await store.ListConversationsAsync("qq", 0, 50))
            .Should().ContainSingle(c => c.Conversation.ExternalId == "g-in-A");

        var directory = new ChannelDirectoryService(store, options);
        var participant = new CommsWorkspaceParticipant(
            [store, directory], NullLogger<CommsWorkspaceParticipant>.Instance);

        // 切到 B：参与者只负责重置目录状态，切换前后的运行时停启不归它管。
        directories.ProjectSeeingDirectory = Path.Combine(_rootB, ".seeing");
        await participant.OnApplyingAsync(new WorkspaceApplyContext(_rootA, _rootB), TestContext.Current.CancellationToken);
        await participant.OnAppliedAsync(new WorkspaceApplyContext(_rootA, _rootB), TestContext.Current.CancellationToken);

        var entries = await store.ListConversationsAsync("qq", 0, 50);
        entries.Should().ContainSingle(c => c.Conversation.ExternalId == "g-in-B");
        entries.Should().NotContain(c => c.Conversation.ExternalId == "g-in-A",
            "旧工作区的对话不得残留在新工作区的目录里");
    }

    /// <summary>
    /// 服务层的回复锚点与频控滑窗也属于旧工作区语义：切换后必须清空，
    /// 否则新工作区的回复会锚到旧工作区的消息、或被旧工作区的计数误限流。
    /// </summary>
    [Fact]
    public async Task OnApplied_ClearsReplyAnchorsAndRateLimitWindows()
    {
        Directory.CreateDirectory(Path.Combine(_rootA, ".seeing", "comms"));
        Directory.CreateDirectory(Path.Combine(_rootB, ".seeing", "comms"));

        var directories = new MutableDirectories(Path.Combine(_rootA, ".seeing"));
        var options = Options.Create(new CommsOptions { RateLimitPerHour = 1 });

        var store = new JsonChannelDirectoryStore(options, directories: directories);
        var directory = new ChannelDirectoryService(store, options);

        var conversationA = new ConversationInfo
        {
            ChannelId = "qq",
            Kind = ConversationKind.Group,
            ExternalId = "g-in-A",
        };

        directory.AdmitInbound(new ChannelInboundMessage
        {
            Sender = new ChannelUserIdentity { ChannelId = "qq", ExternalId = "u1" },
            Conversation = conversationA,
            SessionId = "s",
            ReplyToMsgId = "msg-from-A",
            Text = "hi",
        });
        directory.RecordDelivery("qq", "u1");

        directory.ResolveReplyAnchor(conversationA).Should().Be("msg-from-A");
        directory.IsRateLimited("qq", "u1").Should().BeTrue();

        var participant = new CommsWorkspaceParticipant(
            [store, directory], NullLogger<CommsWorkspaceParticipant>.Instance);

        directories.ProjectSeeingDirectory = Path.Combine(_rootB, ".seeing");
        await participant.OnAppliedAsync(new WorkspaceApplyContext(_rootA, _rootB), TestContext.Current.CancellationToken);

        directory.ResolveReplyAnchor(conversationA).Should().BeNull(
            "回复锚点属于旧工作区，切换后不得把新工作区的回复锚到旧消息上");
        directory.IsRateLimited("qq", "u1").Should().BeFalse(
            "频控滑窗是旧工作区的投递计数，不能拿来限制新工作区");
    }

    [Fact]
    public void ComponentId_ShouldBeComms()
    {
        var store = new JsonChannelDirectoryStore(
            Options.Create(new CommsOptions()),
            NullLogger<JsonChannelDirectoryStore>.Instance);
        var participant = new CommsWorkspaceParticipant(
            [store],
            NullLogger<CommsWorkspaceParticipant>.Instance);

        participant.ComponentId.Should().Be("comms");
    }

    private static ConversationEntry Conversation(string externalId) => new()
    {
        Conversation = new ConversationInfo
        {
            ChannelId = "qq",
            Kind = ConversationKind.Group,
            ExternalId = externalId,
        },
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
        MemberExternalIds = [],
    };

    public void Dispose()
    {
        TryDelete(_rootA);
        TryDelete(_rootB);
        GC.SuppressFinalize(this);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>可变的目录提供者：模拟工作区切换时 ProjectSeeingDirectory 变化。</summary>
    private sealed class MutableDirectories(string projectSeeingDirectory) : ISeeingDirectories
    {
        public string UserSeeingDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

        public string ProjectSeeingDirectory { get; set; } = projectSeeingDirectory;
    }

}
