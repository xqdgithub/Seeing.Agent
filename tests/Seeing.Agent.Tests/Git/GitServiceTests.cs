using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Git;
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
    }
}

public class GitServiceTests
{
    [Fact]
    public async Task IsInRepositoryAsync_WhenNotInRepo_ShouldReturnFalse()
    {
        // Arrange
        var logger = new Mock<ILogger<GitService>>();
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        
        var options = new GitOptions { WorkingDirectory = tempPath };
        var service = new GitService(logger.Object, Mock.Of<IOptions<GitOptions>>(o => o.Value == options));

        // Act
        var result = await service.IsInRepositoryAsync();

        // Assert
        result.Should().BeFalse();

        // Cleanup
        Directory.Delete(tempPath);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_WhenNotInRepo_ShouldReturnHead()
    {
        // Arrange
        var logger = new Mock<ILogger<GitService>>();
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        
        var options = new GitOptions { WorkingDirectory = tempPath };
        var service = new GitService(logger.Object, Mock.Of<IOptions<GitOptions>>(o => o.Value == options));

        // Act
        var branch = await service.GetCurrentBranchAsync();

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
