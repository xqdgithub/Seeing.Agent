using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.FileSystem;
using Seeing.Session.Core;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class AddCollaborationDirectoryToolTests
{
    private sealed class FakeCollaboration : ICollaborationDirectories
    {
        public List<CollaborationDirectory> Items { get; } = new();

        public Task<IReadOnlyList<CollaborationDirectory>> GetAsync(
            string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CollaborationDirectory>>(Items.ToArray());

        public Task AddAsync(
            string sessionId, string absolutePath, string? note = null, CancellationToken ct = default)
        {
            Items.Add(new CollaborationDirectory(absolutePath, note));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, string absolutePath, CancellationToken ct = default)
        {
            Items.RemoveAll(item => item.Path == absolutePath);
            return Task.CompletedTask;
        }
    }

    private static AddCollaborationDirectoryTool CreateTool(FakeCollaboration? collaboration = null)
        => new(
            NullLogger<AddCollaborationDirectoryTool>.Instance,
            new Seeing.IO.Local.LocalFileSystem(),
            collaboration ?? new FakeCollaboration());

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "collab_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ToolContext Context(IPermissionAuthorizer? authorizer, string cwd) => new()
    {
        SessionId = "s1",
        CallId = "c1",
        WorkingDirectory = cwd,
        PermissionAuthorizer = authorizer
    };

    private static IPermissionAuthorizer Allow(PermissionGrantScope scope)
    {
        var mock = new Mock<IPermissionAuthorizer>();
        mock.Setup(a => a.AuthorizeAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PermissionResolution
            {
                RequestId = "r1",
                SessionId = "s1",
                Decision = PermissionEffect.Allow,
                Scope = scope
            });
        return mock.Object;
    }

    [Fact]
    public async Task Execute_NoAuthorizer_ShouldFail()
    {
        var result = await CreateTool().ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = NewDir() }),
            Context(authorizer: null, Path.GetTempPath()));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("审批");
    }

    [Fact]
    public async Task Execute_OnceScope_ShouldNotPersist()
    {
        var collaboration = new FakeCollaboration();
        var dir = NewDir();

        var result = await CreateTool(collaboration).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = dir }),
            Context(Allow(PermissionGrantScope.Once), Path.GetTempPath()));

        result.Success.Should().BeFalse();
        collaboration.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_SessionDirectoryScope_ShouldPersist()
    {
        var collaboration = new FakeCollaboration();
        var dir = NewDir();

        var result = await CreateTool(collaboration).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = dir, note = "报表" }),
            Context(Allow(PermissionGrantScope.SessionDirectory), Path.GetTempPath()));

        result.Success.Should().BeTrue();
        collaboration.Items.Should().ContainSingle();
        collaboration.Items[0].Path.Should().Be(dir);
        collaboration.Items[0].Note.Should().Be("报表");
    }

    [Fact]
    public async Task Execute_RelativePath_ShouldResolveAgainstContextCwd()
    {
        var collaboration = new FakeCollaboration();
        var cwd = NewDir();
        Directory.CreateDirectory(Path.Combine(cwd, "sub"));

        var result = await CreateTool(collaboration).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = "sub" }),
            Context(Allow(PermissionGrantScope.Session), cwd));

        result.Success.Should().BeTrue();
        collaboration.Items.Should().ContainSingle()
            .Which.Path.Should().Be(Path.Combine(cwd, "sub"));
    }
}
