using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Tools.Git;
using Xunit;

namespace Seeing.Agent.Tests.Git;

public class GitServiceSubprocessTests
{
    [Fact]
    public async Task GetStatusAsync_WithFakeSubprocess_ShouldParseWithoutOsGit()
    {
        const string statusOutput = """
            # branch.head feature/test
            # branch.ab +2 -1
            1 .M N... 100644 100644 100644 abc def src/file.cs
            ? README.md
            """;

        var factory = new FakeSubprocessFactory(spec =>
        {
            spec.FileName.Should().Be("git");
            spec.Arguments.Should().Be("status --porcelain=v2 --branch");
            spec.WorkingDirectory.Should().Be("/repo");

            return new FakeSubprocess(statusOutput, exitCode: 0);
        });

        var world = new FakeExecutionWorld("/repo", factory);
        var service = new GitService(
            Mock.Of<ILogger<GitService>>(),
            world,
            CreateMonitor(new GitOptions()));

        var status = await service.GetStatusAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        status.Branch.Should().Be("feature/test");
        status.Ahead.Should().Be("+2");
        status.Behind.Should().Be("-1");
        status.IsClean.Should().BeFalse();
        status.Files.Should().HaveCount(2);
        status.Files[0].Path.Should().Be("src/file.cs");
        status.Files[0].State.Should().Be(GitFileState.Modified);
        status.Files[1].State.Should().Be(GitFileState.Untracked);
        factory.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkingDirectoryNotConfigured_ShouldFallbackToWorldCwd()
    {
        SubprocessSpec? captured = null;
        var factory = new FakeSubprocessFactory(spec =>
        {
            captured = spec;
            return new FakeSubprocess("", exitCode: 0);
        });

        var world = new FakeExecutionWorld("/world-cwd", factory);
        var service = new GitService(
            Mock.Of<ILogger<GitService>>(),
            world,
            CreateMonitor(new GitOptions()));

        await service.ExecuteAsync(
            "rev-parse",
            ["--is-inside-work-tree"],
            TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.WorkingDirectory.Should().Be("/world-cwd");
    }

    private static IOptionsMonitor<GitOptions> CreateMonitor(GitOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<GitOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        return monitor.Object;
    }

    private sealed class FakeExecutionWorld(string cwd, ISubprocessFactory subprocess) : IExecutionWorld
    {
        public string Cwd { get; } = cwd;
        public IFileSystem FileSystem => throw new NotSupportedException();
        public ISubprocessFactory Subprocess { get; } = subprocess;
    }

    private sealed class FakeSubprocessFactory(Func<SubprocessSpec, FakeSubprocess> onStart) : ISubprocessFactory
    {
        public int StartCount { get; private set; }

        public ISubprocess Start(SubprocessSpec spec)
        {
            StartCount++;
            return onStart(spec);
        }
    }

    private sealed class FakeSubprocess : ISubprocess
    {
        private readonly StringReader _stdout;
        private readonly StringReader _stderr;

        public FakeSubprocess(string stdout, string stderr = "", int exitCode = 0)
        {
            _stdout = new StringReader(stdout);
            _stderr = new StringReader(stderr);
            ExitCode = exitCode;
            HasExited = true;
        }

        public int Id => 1;
        public TextReader StandardOutput => _stdout;
        public TextReader StandardError => _stderr;
        public bool HasExited { get; private set; }
        public int ExitCode { get; }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            HasExited = true;
            return Task.CompletedTask;
        }

        public void Kill(bool entireTree = true)
        {
        }

        public void Dispose()
        {
            _stdout.Dispose();
            _stderr.Dispose();
        }
    }
}
