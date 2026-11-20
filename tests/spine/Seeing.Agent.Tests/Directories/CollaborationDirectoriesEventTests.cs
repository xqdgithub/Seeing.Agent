using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Directories;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Directories;

/// <summary>
/// 协同目录写端口必须发布明确事件：UI 靠它重载根列表，而不是定时轮询。
/// </summary>
public class CollaborationDirectoriesEventTests
{
    [Fact]
    public async Task Add_NonExistentDirectory_ShouldThrow()
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(Path.GetTempPath(), "cd_missing_" + Guid.NewGuid().ToString("N"));

        var act = async () => await fixture.Directories.AddAsync("s1", missing);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Add_ProjectRoot_ShouldThrow()
    {
        using var fixture = new Fixture();

        var act = async () => await fixture.Directories.AddAsync("s1", fixture.ProjectRoot);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*工作区根*");
    }

    [Fact]
    public async Task Add_ChildOfExisting_ShouldThrow()
    {
        using var fixture = new Fixture();
        var parent = NewDirectory();
        var child = Path.Combine(parent, "sub");
        Directory.CreateDirectory(child);

        await fixture.Directories.AddAsync("s1", parent);
        var act = async () => await fixture.Directories.AddAsync("s1", child);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*重叠*");
    }

    [Fact]
    public async Task Add_ParentOfExisting_ShouldThrow()
    {
        using var fixture = new Fixture();
        var parent = NewDirectory();
        var child = Path.Combine(parent, "sub");
        Directory.CreateDirectory(child);

        await fixture.Directories.AddAsync("s1", child);
        var act = async () => await fixture.Directories.AddAsync("s1", parent);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*重叠*");
    }

    [Fact]
    public async Task Add_SameDirectory_ShouldUpdateNoteWithoutThrowing()
    {
        using var fixture = new Fixture();
        var dir = NewDirectory();

        await fixture.Directories.AddAsync("s1", dir, note: "旧");
        await fixture.Directories.AddAsync("s1", dir, note: "新");

        fixture.Session.CollaborationDirectories.Should().ContainSingle()
            .Which.Note.Should().Be("新");
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class Collector : IObserver<SessionEvent>
    {
        public List<SessionEvent> Events { get; } = [];

        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(SessionEvent value) => Events.Add(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _subscription;

        public Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "cd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            ProjectRoot = root;

            Session = SessionData.Create("p1", "build");
            Session.Id = "s1";

            var sessions = new Mock<ISessionManager>();
            sessions.Setup(s => s.Get("s1")).Returns(Session);
            sessions.Setup(s => s.UpdateSessionAsync(
                    "s1", It.IsAny<Action<SessionData>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, Action<SessionData> update, CancellationToken __) =>
                {
                    update(Session);
                    return Session;
                });

            var workspace = new Mock<IWorkspaceProvider>();
            workspace.SetupGet(w => w.ProjectSeeingDirectory).Returns(Path.Combine(root, ".seeing"));

            var publisher = new SessionEventPublisher();
            _subscription = publisher.Events.Subscribe(Collector);

            Directories = new CollaborationDirectories(
                sessions.Object, workspace.Object, publisher, NullLogger<CollaborationDirectories>.Instance);
        }

        public SessionData Session { get; }
        public CollaborationDirectories Directories { get; }
        public Collector Collector { get; } = new();
        public string ProjectRoot { get; }

        public void Dispose() => _subscription.Dispose();
    }

    [Fact]
    public async Task Add_ShouldPublishCollaborationDirectoriesChanged()
    {
        using var fixture = new Fixture();
        var target = NewDirectory();

        await fixture.Directories.AddAsync("s1", target);

        fixture.Collector.Events.Should().ContainSingle(e =>
            e.Type == SessionEventType.CollaborationDirectoriesChanged && e.SessionId == "s1");
    }

    [Fact]
    public async Task Remove_ShouldPublishCollaborationDirectoriesChanged()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(Path.GetTempPath(), "cd_rm_" + Guid.NewGuid().ToString("N"));
        fixture.Session.CollaborationDirectories.Add(new CollaborationDirectory(target));
        fixture.Collector.Events.Clear();

        await fixture.Directories.RemoveAsync("s1", target);

        fixture.Collector.Events.Should().ContainSingle(e =>
            e.Type == SessionEventType.CollaborationDirectoriesChanged && e.SessionId == "s1");
    }
}
