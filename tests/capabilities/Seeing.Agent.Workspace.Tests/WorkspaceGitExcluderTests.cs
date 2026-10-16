using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Workspace.Tests;

/// <summary>
/// <see cref="WorkspaceGitExcluder"/> 真实 git 集成测试：在临时仓库中验证
/// <c>.git/info/exclude</c> 的幂等写入、探测短路与锚定模式。
/// </summary>
public class WorkspaceGitExcluderTests : IDisposable
{
    private readonly string _root;
    private readonly LocalExecutionWorld _world = new();
    private readonly WorkspaceGitExcluder _sut;

    public WorkspaceGitExcluderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seeing-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sut = new WorkspaceGitExcluder(
            _world.Subprocess,
            _world.FileSystem,
            NullLogger<WorkspaceGitExcluder>.Instance);
    }

    [Fact]
    public async Task NonGitDirectory_ShouldReturnNotARepository()
    {
        var outcome = await _sut.EnsureExcludedAsync(_root, TestContext.Current.CancellationToken);

        outcome.Should().Be(GitExcludeOutcome.NotARepository);
        File.Exists(Path.Combine(_root, ".git", "info", "exclude")).Should().BeFalse();
    }

    [Fact]
    public async Task FreshRepo_ShouldAppendMarkerBlock_AndIgnoreSeeing()
    {
        InitRepo();

        var outcome = await _sut.EnsureExcludedAsync(_root, TestContext.Current.CancellationToken);

        outcome.Should().Be(GitExcludeOutcome.Applied);
        var exclude = File.ReadAllText(Path.Combine(_root, ".git", "info", "exclude"));
        exclude.Should().Contain(WorkspaceGitExcluder.MarkerBegin);
        exclude.Should().Contain("/.seeing/");
        exclude.Should().Contain(WorkspaceGitExcluder.MarkerEnd);

        // git 侧确认已忽略，且 .gitignore 未被创建
        GitCheckIgnore(".seeing/").Should().BeTrue();
        File.Exists(Path.Combine(_root, ".gitignore")).Should().BeFalse();
    }

    [Fact]
    public async Task SecondRun_ShouldBeIdempotent_NoDuplicateBlock()
    {
        InitRepo();

        (await _sut.EnsureExcludedAsync(_root, TestContext.Current.CancellationToken))
            .Should().Be(GitExcludeOutcome.Applied);

        var second = await _sut.EnsureExcludedAsync(_root, TestContext.Current.CancellationToken);

        // 首次写入后 check-ignore 命中 → 第二次短路为 AlreadyIgnored，不重复写
        second.Should().Be(GitExcludeOutcome.AlreadyIgnored);
        var exclude = File.ReadAllText(Path.Combine(_root, ".git", "info", "exclude"));
        CountOccurrences(exclude, WorkspaceGitExcluder.MarkerBegin).Should().Be(1);
    }

    [Fact]
    public async Task RepoAlreadyIgnoringViaGitignore_ShouldNotTouchExclude()
    {
        InitRepo();
        var gitignore = Path.Combine(_root, ".gitignore");
        File.WriteAllText(gitignore, ".seeing/\n");
        var excludePath = Path.Combine(_root, ".git", "info", "exclude");
        var before = File.ReadAllText(excludePath);

        var outcome = await _sut.EnsureExcludedAsync(_root, TestContext.Current.CancellationToken);

        outcome.Should().Be(GitExcludeOutcome.AlreadyIgnored);
        File.ReadAllText(excludePath).Should().Be(before);
    }

    [Fact]
    public async Task WorkspaceInSubdirectory_ShouldAnchorPatternToRepoRoot()
    {
        InitRepo();
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        var outcome = await _sut.EnsureExcludedAsync(sub, TestContext.Current.CancellationToken);

        outcome.Should().Be(GitExcludeOutcome.Applied);
        var exclude = File.ReadAllText(Path.Combine(_root, ".git", "info", "exclude"));
        exclude.Should().Contain("/sub/.seeing/");
    }

    [Fact]
    public async Task ExistingContent_ShouldBePreserved()
    {
        InitRepo();
        var excludePath = Path.Combine(_root, ".git", "info", "exclude");
        File.WriteAllText(excludePath, "# my rules\r\n*.log\r\n");

        var outcome = await _sut.EnsureExcludedAsync(_root, TestContext.Current.CancellationToken);

        outcome.Should().Be(GitExcludeOutcome.Applied);
        var exclude = File.ReadAllText(excludePath);
        exclude.Should().StartWith("# my rules\r\n*.log\r\n");
        // 保留 CRLF 换行风格
        exclude.Should().Contain("# my rules\r\n*.log\r\n" + WorkspaceGitExcluder.MarkerBegin);
    }

    private void InitRepo()
    {
        RunGit("init", "-q");
        RunGit("config", "user.email", "t@t");
        RunGit("config", "user.name", "t");
    }

    private bool GitCheckIgnore(string path)
    {
        var (exit, _) = RunGitRaw("check-ignore", "-q", "--no-index", "--", path);
        return exit == 0;
    }

    private void RunGit(params string[] args)
    {
        var (exit, stderr) = RunGitRaw(args);
        exit.Should().Be(0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private (int Exit, string StdErr) RunGitRaw(params string[] args)
    {
        var spec = new SubprocessSpec
        {
            FileName = "git",
            Arguments = string.Join(' ', args.Select(WorkspaceGitExcluder.Quote)),
            WorkingDirectory = _root,
            CancellationToken = TestContext.Current.CancellationToken,
        };

        using var process = _world.Subprocess.Start(spec);
        var stdout = process.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
        var stderr = process.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
        process.WaitForExitAsync().GetAwaiter().GetResult();
        _ = stdout;
        return (process.ExitCode, stderr);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort 清理
        }
    }
}
