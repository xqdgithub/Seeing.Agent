using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests
{
    /// <summary>
    /// SessionManager 存储加载测试（冷缓存恢复场景）
    /// </summary>
    public class SessionManagerStorageTests
    {
        private static SessionManager CreateManager(InMemorySessionStore store) =>
            new(store: store, logger: new NullLogger<SessionManager>());

        [Fact]
        public async Task LoadChildrenFromStorageAsync_ShouldLoadSubAgentChildrenAndPreserveMetadata()
        {
            var store = new InMemorySessionStore();
            var mgr = CreateManager(store);

            var parent = mgr.Create(partitionId: "p1");
            parent.Kind = SessionKind.Root;
            mgr.Register(parent);

            // 直接写入存储（模拟磁盘上的子会话，绕过内存缓存 = 冷缓存）
            var child = new SessionData
            {
                Id = "child_1",
                Title = "Sub Task (@explore)",
                ParentSessionId = parent.Id,
                Kind = SessionKind.SubAgent,
                PartitionId = parent.PartitionId,
                Metadata = { [SessionMetadataKeys.OriginToolCallId] = "toolcall_abc" }
            };
            await store.SaveAsync(child);

            // 冷缓存：内存缓存中无子会话
            var before = await mgr.ListChildrenAsync(parent.Id, SessionKind.SubAgent);
            before.Should().BeEmpty();

            // 从存储加载子会话
            var loaded = await mgr.LoadChildrenFromStorageAsync(parent.Id);

            loaded.Should().HaveCount(1);
            loaded[0].Id.Should().Be("child_1");
            loaded[0].Kind.Should().Be(SessionKind.SubAgent);
            loaded[0].Metadata[SessionMetadataKeys.OriginToolCallId].Should().Be("toolcall_abc");

            // 已注册到缓存，可直接查询
            var cached = mgr.Get("child_1");
            cached.Should().NotBeNull();
            cached!.Metadata[SessionMetadataKeys.OriginToolCallId].Should().Be("toolcall_abc");
        }

        [Fact]
        public async Task LoadChildrenFromStorageAsync_ShouldSkipChildrenAlreadyInCache()
        {
            var store = new InMemorySessionStore();
            var mgr = CreateManager(store);

            var parent = mgr.Create();
            parent.Kind = SessionKind.Root;
            mgr.Register(parent);

            // 磁盘上的旧快照
            var diskSnapshot = new SessionData
            {
                Id = "child_1",
                Title = "磁盘旧快照",
                ParentSessionId = parent.Id,
                Kind = SessionKind.SubAgent,
                PartitionId = parent.PartitionId
            };
            await store.SaveAsync(diskSnapshot);

            // 内存缓存中的活跃子会话（较新状态）
            var cachedChild = new SessionData
            {
                Id = "child_1",
                Title = "缓存新状态",
                ParentSessionId = parent.Id,
                Kind = SessionKind.SubAgent,
                PartitionId = parent.PartitionId
            };
            mgr.Register(cachedChild);

            // 加载后缓存中的值不应被磁盘旧快照覆盖
            var loaded = await mgr.LoadChildrenFromStorageAsync(parent.Id);

            loaded.Should().HaveCount(1);
            loaded[0].Id.Should().Be("child_1");
            loaded[0].Title.Should().Be("缓存新状态");

            var cached = mgr.Get("child_1");
            cached.Should().NotBeNull();
            cached!.Title.Should().Be("缓存新状态");
        }

        [Fact]
        public async Task LoadChildrenFromStorageAsync_ShouldIgnoreNonSubAgentChildren()
        {
            var store = new InMemorySessionStore();
            var mgr = CreateManager(store);

            var parent = mgr.Create();
            parent.Kind = SessionKind.Root;
            mgr.Register(parent);

            var fork = new SessionData
            {
                Id = "fork_1",
                Title = "Fork 分支",
                ParentSessionId = parent.Id,
                Kind = SessionKind.Fork,
                PartitionId = parent.PartitionId
            };
            await store.SaveAsync(fork);

            var loaded = await mgr.LoadChildrenFromStorageAsync(parent.Id);

            loaded.Should().BeEmpty();
        }
    }
}
