using System.Linq;
using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class FileSessionGroupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "seeing-group-store-" + Guid.NewGuid().ToString("N"));

    private static SessionGroup MakeGroup(string id, params string[] sessionIds) => new()
    {
        Id = id,
        PartitionId = "p1",
        Title = "组-" + id,
        AnchorSessionId = sessionIds[0],
        ActiveSessionId = sessionIds[0],
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now,
        Version = 1,
        Members = sessionIds.Select((sid, i) => new SessionGroupMember
        {
            SessionId = sid,
            IsAnchor = i == 0,
            Relation = i == 0 ? SessionRelation.None : SessionRelation.Child,
            ParentSessionId = i == 0 ? null : sessionIds[0],
            Order = i
        }).ToList()
    };

    [Fact]
    public async Task SaveAndLoad_ShouldRoundTrip()
    {
        var store = new FileSessionGroupStore(_dir);

        await store.SaveAsync(MakeGroup("grp_1", "s1", "s2"), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync("grp_1", TestContext.Current.CancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("grp_1");
        loaded.PartitionId.Should().Be("p1");
        loaded.AnchorSessionId.Should().Be("s1");
        loaded.Members.Should().HaveCount(2);
        loaded.Members[1].SessionId.Should().Be("s2");
        loaded.Members[1].Relation.Should().Be(SessionRelation.Child);
        loaded.Members[1].ParentSessionId.Should().Be("s1");
        loaded.Version.Should().Be(1);
    }

    [Fact]
    public async Task LoadAsync_WhenMissing_ShouldReturnNull()
    {
        var store = new FileSessionGroupStore(_dir);

        (await store.LoadAsync("grp_none", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task FindBySessionAsync_ShouldMatchAnyMember()
    {
        var store = new FileSessionGroupStore(_dir);
        await store.SaveAsync(MakeGroup("grp_1", "s1", "s2"), TestContext.Current.CancellationToken);
        await store.SaveAsync(MakeGroup("grp_2", "s3"), TestContext.Current.CancellationToken);

        var found = await store.FindBySessionAsync("s2", TestContext.Current.CancellationToken);
        var anchor = await store.FindBySessionAsync("s3", TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
        found!.Id.Should().Be("grp_1");
        anchor.Should().NotBeNull();
        anchor!.Id.Should().Be("grp_2");
        (await store.FindBySessionAsync("missing", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveFile()
    {
        var store = new FileSessionGroupStore(_dir);
        await store.SaveAsync(MakeGroup("grp_1", "s1"), TestContext.Current.CancellationToken);

        await store.DeleteAsync("grp_1", TestContext.Current.CancellationToken);

        (await store.LoadAsync("grp_1", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task SetBaseDirectory_ShouldRelocateReadsAndWrites()
    {
        var dir2 = Path.Combine(Path.GetTempPath(), "seeing-group-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionGroupStore(_dir);
            await store.SaveAsync(MakeGroup("grp_old", "s1"), TestContext.Current.CancellationToken);

            store.SetBaseDirectory(dir2);
            store.BaseDirectory.Should().Be(dir2);

            (await store.LoadAsync("grp_old", TestContext.Current.CancellationToken)).Should().BeNull();

            await store.SaveAsync(MakeGroup("grp_new", "s2"), TestContext.Current.CancellationToken);
            (await store.LoadAsync("grp_new", TestContext.Current.CancellationToken)).Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
        }
    }

    [Fact]
    public void BaseDirectory_ShouldDefaultToUserSessionGroups()
    {
        var store = new FileSessionGroupStore();

        store.BaseDirectory.Should().EndWith(Path.Combine(".seeing", "session-groups"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
