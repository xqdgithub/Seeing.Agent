using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Acp.Filesystem;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

/// <summary>
/// ACP 文件桥读取：流式限长（超出上限即截断，不整文件入内存）。
/// </summary>
public class AcpFileSystemBridgeReadTests
{
    [Fact]
    public async Task ReadTextFileAsync_LargeFile_ShouldTruncateToByteLimit()
    {
        // Arrange：写入 100KB 纯 ASCII，超出 50KB 上限
        var root = NewWorkspace();
        var path = Path.Combine(root, "big.txt");
        await File.WriteAllTextAsync(path, new string('a', 100_000), TestContext.Current.CancellationToken);

        var bridge = new AcpFileSystemBridge(NullLogger<AcpFileSystemBridge>.Instance);

        try
        {
            // Act
            var response = await bridge.ReadTextFileAsync(
                "big.txt", "sess", root, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            response.Content.Length.Should().Be(50 * 1024);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadTextFileAsync_SpecificLine_ShouldReturnThatLine()
    {
        // Arrange
        var root = NewWorkspace();
        var path = Path.Combine(root, "lines.txt");
        await File.WriteAllTextAsync(path, "line1\nline2\nline3\n", TestContext.Current.CancellationToken);

        var bridge = new AcpFileSystemBridge(NullLogger<AcpFileSystemBridge>.Instance);

        try
        {
            // Act
            var response = await bridge.ReadTextFileAsync(
                "lines.txt", "sess", root, line: 2, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            response.Content.Should().Be("line2");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadTextFileAsync_MissingFile_ShouldReturnEmpty()
    {
        var root = NewWorkspace();
        var bridge = new AcpFileSystemBridge(NullLogger<AcpFileSystemBridge>.Instance);

        try
        {
            var response = await bridge.ReadTextFileAsync(
                "missing.txt", "sess", root, cancellationToken: TestContext.Current.CancellationToken);

            response.Content.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"acp-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
