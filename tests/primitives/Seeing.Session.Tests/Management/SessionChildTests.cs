using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Management
{
    /// <summary>
    /// SessionManager 已降级为无关系 CRUD；关系语义迁往 SessionGroupManager。
    /// SessionForker 为纯消息复制引擎，不写 Kind/关系。
    /// </summary>
    public class SessionChildTests
    {
        private static SessionManager CreateManager() =>
            new SessionManager(logger: new NullLogger<SessionManager>());

        private static SessionForker CreateForker(SessionManager manager) =>
            new SessionForker(new NullLogger<SessionForker>(), manager);

        [Theory]
        [InlineData("ForkAsync")]
        [InlineData("CreateChildAsync")]
        [InlineData("ListChildrenAsync")]
        [InlineData("ListRootsAsync")]
        [InlineData("LoadChildrenFromStorageAsync")]
        public void ISessionManager_ShouldNotExposeRelationMethods(string methodName)
        {
            var methodNames = typeof(ISessionManager)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name);

            methodNames.Should().NotContain(methodName);
        }

        [Fact]
        public async Task ForkAsync_ShouldCloneMessagesWithNewIdsAndOwnership()
        {
            var mgr = CreateManager();
            var source = mgr.Create(partitionId: "p1", selectedAgent: "build");
            source.AddMessage(new SessionMessage { Id = "m1", Role = "user", Content = "hello", CreatedAt = DateTime.UtcNow });
            source.AddMessage(new SessionMessage { Id = "m2", Role = "assistant", Content = "hi", CreatedAt = DateTime.UtcNow });
            mgr.Register(source);

            var forked = await CreateForker(mgr).ForkAsync(source.Id);

            forked.Id.Should().NotBe(source.Id);
            forked.Messages.Should().HaveCount(2);
            forked.Messages.Select(m => m.Content).Should().Equal("hello", "hi");
            forked.Messages.Select(m => m.Id).Should().NotIntersectWith(source.Messages.Select(m => m.Id));
            forked.Messages.Should().OnlyContain(m => m.SessionId == forked.Id);
        }

        [Fact]
        public async Task ForkAsync_ShouldCopyConfigAndPreserveTitleWhenNoLabel()
        {
            var mgr = CreateManager();
            var source = mgr.Create(partitionId: "p1", selectedAgent: "build", scenario: "code");
            source.Title = "源会话";
            source.SelectedModel = "openai/gpt-4o";
            source.SelectedThinkingEffort = "high";
            source.WorkingDirectory = @"E:\work";
            source.ScenarioOverride = new SessionScenarioOverride
            {
                Tools = new SessionToolsOverride { Disabled = ["shell"] }
            };
            mgr.Register(source);

            var forked = await CreateForker(mgr).ForkAsync(source.Id);

            forked.PartitionId.Should().Be("p1");
            forked.SelectedAgent.Should().Be("build");
            forked.SelectedModel.Should().Be("openai/gpt-4o");
            forked.SelectedThinkingEffort.Should().Be("high");
            forked.Scenario.Should().Be("code");
            forked.WorkingDirectory.Should().Be(@"E:\work");
            forked.ScenarioOverride.Should().NotBeNull();
            forked.ScenarioOverride!.Tools!.Disabled.Should().Equal("shell");
            forked.ScenarioOverride.Should().NotBeSameAs(source.ScenarioOverride);
            forked.Title.Should().Be("源会话");
        }

        [Fact]
        public async Task ForkAsync_WithLabel_ShouldUseLabelAsTitle()
        {
            var mgr = CreateManager();
            var source = mgr.Create();
            mgr.Register(source);

            var forked = await CreateForker(mgr).ForkAsync(source.Id, "新分支");

            forked.Title.Should().Be("新分支");
        }

        [Fact]
        public async Task ForkAsync_ShouldNotWriteKindOrRelation()
        {
            var mgr = CreateManager();
            var source = mgr.Create();
            source.Kind = SessionKind.SubAgent;
            source.GroupId = "g1";
            mgr.Register(source);

            var forked = await CreateForker(mgr).ForkAsync(source.Id, "label");

            // 不继承源 Kind（若复制则为 SubAgent）；默认 Root，由调用方（组管理器）按场景设定
            forked.Kind.Should().Be(SessionKind.Root);
            // 组归属仅由 SessionGroupManager 写入
            forked.GroupId.Should().BeNull();
        }

        [Fact]
        public async Task ForkAsync_ShouldRegisterAndPersistNewSession()
        {
            var store = new InMemorySessionStore();
            var mgr = new SessionManager(store: store, logger: new NullLogger<SessionManager>());
            var source = mgr.Create();
            mgr.Register(source);

            var forked = await CreateForker(mgr).ForkAsync(source.Id);

            mgr.Get(forked.Id).Should().BeSameAs(forked);
            var loaded = await store.LoadAsync(forked.Id);
            loaded.Should().NotBeNull();
            loaded!.Id.Should().Be(forked.Id);
        }

        [Fact]
        public async Task ForkAsync_WhenSourceMissing_ShouldThrow()
        {
            var mgr = CreateManager();

            var act = () => CreateForker(mgr).ForkAsync("missing");

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }
}
