using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Session.Tests.Management
{
    public class SessionChildTests
    {
        private static SessionManager CreateManager() =>
            new SessionManager(logger: new NullLogger<SessionManager>());

        [Fact]
        public async Task CreateChildAsync_ShouldSetSubAgentKindAndEmptyHistory()
        {
            var mgr = CreateManager();
            var parent = mgr.Create(partitionId: "p1", selectedAgent: "build");
            parent.Kind = SessionKind.Root;
            parent.WorkingDirectory = @"E:\work";
            mgr.Register(parent);

            var rules = new List<SessionPermissionRule>
            {
                new() { Kind = "Tool", Pattern = "task", Effect = "Deny", Priority = 100 }
            };
            var child = await mgr.CreateChildAsync(parent.Id, "explore", "Find auth (@explore)", rules);

            child.Kind.Should().Be(SessionKind.SubAgent);
            child.ParentSessionId.Should().Be(parent.Id);
            child.SelectedAgent.Should().Be("explore");
            child.Title.Should().Be("Find auth (@explore)");
            child.Messages.Should().BeEmpty();
            child.PermissionSnapshot.Should().HaveCount(1);
            child.WorkingDirectory.Should().Be(parent.WorkingDirectory);
            child.PartitionId.Should().Be(parent.PartitionId);
        }

        [Fact]
        public async Task ListRootsAsync_ShouldExcludeForkAndSubAgent()
        {
            var mgr = CreateManager();
            var root = mgr.Create();
            root.Kind = SessionKind.Root;
            mgr.Register(root);

            var fork = await mgr.ForkAsync(root.Id, label: "f1");
            fork.Kind.Should().Be(SessionKind.Fork);

            await mgr.CreateChildAsync(root.Id, "explore", "t", Array.Empty<SessionPermissionRule>());

            var roots = await mgr.ListRootsAsync();
            roots.Select(r => r.Id).Should().Equal(root.Id);
        }

        [Fact]
        public async Task ListChildrenAsync_FilterByKind_ShouldDistinguishForkAndSubAgent()
        {
            var mgr = CreateManager();
            var root = mgr.Create();
            root.Kind = SessionKind.Root;
            mgr.Register(root);

            await mgr.ForkAsync(root.Id, "f");
            await mgr.CreateChildAsync(root.Id, "explore", "t", Array.Empty<SessionPermissionRule>());

            var subs = await mgr.ListChildrenAsync(root.Id, SessionKind.SubAgent);
            subs.Should().HaveCount(1);
            subs[0].Kind.Should().Be(SessionKind.SubAgent);
        }

        [Fact]
        public async Task ForkAsync_FromSubAgent_ShouldCreateIndependentRoot()
        {
            var mgr = CreateManager();
            var parent = mgr.Create(selectedAgent: "build");
            parent.Kind = SessionKind.Root;
            parent.SelectedModel = "gpt-4o";
            parent.SelectedModelProvider = "openai";
            mgr.Register(parent);

            var child = await mgr.CreateChildAsync(
                parent.Id, "explore", "task", Array.Empty<SessionPermissionRule>());
            child.Messages.Add(new SessionMessage
            {
                Id = "m1",
                Role = "user",
                Content = "hello",
                CreatedAt = DateTime.UtcNow
            });
            await mgr.SaveAsync(child.Id);

            var detached = await mgr.ForkAsync(child.Id, label: "独立");

            detached.Kind.Should().Be(SessionKind.Root);
            detached.ParentSessionId.Should().BeNull();
            detached.SelectedModel.Should().Be("gpt-4o");
            detached.SelectedModelProvider.Should().Be("openai");
            detached.Messages.Should().HaveCount(1);
            detached.Id.Should().NotBe(child.Id);

            // 源子会话仍为 SubAgent 只读
            var stillChild = mgr.Get(child.Id);
            stillChild!.Kind.Should().Be(SessionKind.SubAgent);
            stillChild.ParentSessionId.Should().Be(parent.Id);
        }
    }
}
