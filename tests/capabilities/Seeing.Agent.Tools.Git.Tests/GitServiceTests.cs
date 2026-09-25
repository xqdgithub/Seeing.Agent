using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Core.Tools.Git;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Tests.Git;

public class GitModelsTests
{
    [Fact]
    public void GitStatus_Defaults()
    {
        // Arrange & Act
        var status = new GitStatus();

        // Assert
        status.Branch.Should().BeEmpty();
        status.IsClean.Should().BeFalse();
        status.Files.Should().BeEmpty();
    }

    [Fact]
    public void GitCommit_ShortHash_ShouldTruncate()
    {
        // Arrange
        var commit = new GitCommit
        {
            Hash = "abc123def456789012345678901234567890abcd"
        };

        // Act
        var shortHash = commit.ShortHash;

        // Assert
        shortHash.Should().Be("abc123d");
        shortHash.Length.Should().Be(7);
    }

    [Fact]
    public void GitCommit_ShortHash_ShortHash_ShouldReturnAsIs()
    {
        // Arrange
        var commit = new GitCommit
        {
            Hash = "abc"
        };

        // Act
        var shortHash = commit.ShortHash;

        // Assert
        shortHash.Should().Be("abc");
    }

    [Fact]
    public void GitDiff_Defaults()
    {
        // Arrange & Act
        var diff = new GitDiff();

        // Assert
        diff.Files.Should().BeEmpty();
        diff.TotalAddedLines.Should().Be(0);
        diff.TotalDeletedLines.Should().Be(0);
    }

    [Fact]
    public void GitBranch_Defaults()
    {
        // Arrange & Act
        var branch = new GitBranch();

        // Assert
        branch.Name.Should().BeEmpty();
        branch.IsCurrent.Should().BeFalse();
        branch.IsRemote.Should().BeFalse();
    }

    [Fact]
    public void GitOptions_Defaults()
    {
        // Arrange & Act
        var options = new GitOptions();

        // Assert
        options.GitPath.Should().Be("git");
        options.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        options.WorkingDirectory.Should().BeNull();
    }
}

public class GitServiceTests
{
    private static GitService CreateService(string workingDirectory)
    {
        var logger = new Mock<ILogger<GitService>>();
        var world = new LocalExecutionWorld();
        var options = new GitOptions { WorkingDirectory = workingDirectory };
        return new GitService(
            logger.Object,
            world,
            Mock.Of<IOptionsMonitor<GitOptions>>(o => o.CurrentValue == options));
    }

    [Fact]
    public async Task IsInRepositoryAsync_WhenNotInRepo_ShouldReturnFalse()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        var service = CreateService(tempPath);

        // Act
        var result = await service.IsInRepositoryAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeFalse();

        // Cleanup
        Directory.Delete(tempPath);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_WhenNotInRepo_ShouldReturnHead()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        var service = CreateService(tempPath);

        // Act
        var branch = await service.GetCurrentBranchAsync(TestContext.Current.CancellationToken);

        // Assert
        branch.Should().Be("HEAD");

        // Cleanup
        Directory.Delete(tempPath);
    }
}

public class GitExceptionTests
{
    [Fact]
    public void FromResult_ShouldCreateException()
    {
        // Arrange
        var result = new GitResult
        {
            Success = false,
            ExitCode = 1,
            StdOut = "output",
            StdErr = "error message"
        };

        // Act
        var ex = GitException.FromResult(result, "status");

        // Assert
        ex.Message.Should().Contain("error message");
        ex.ExitCode.Should().Be(1);
        ex.GitCommand.Should().Be("status");
    }
}
