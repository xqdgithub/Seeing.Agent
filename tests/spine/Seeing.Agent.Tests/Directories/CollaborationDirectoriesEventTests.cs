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

        public void Dispose() => _subscription.Dispose();
    }

    [Fact]
    public async Task Add_ShouldPublishCollaborationDirectoriesChanged()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(Path.GetTempPath(), "cd_add_" + Guid.NewGuid().ToString("N"));

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
