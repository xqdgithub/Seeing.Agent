using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Instructions;
using Xunit;

namespace Seeing.Agent.Tests.Instructions
{
    /// <summary>
    /// InstructionLoader 单元测试
    /// </summary>
    public class InstructionLoaderTests : IDisposable
    {
        private readonly Mock<ILogger<InstructionLoader>> _loggerMock;
        private readonly InstructionLoader _loader;
        private readonly string _testDirectory;

        public InstructionLoaderTests()
        {
            _loggerMock = new Mock<ILogger<InstructionLoader>>();
            _loader = new InstructionLoader(_loggerMock.Object);
            _testDirectory = Path.Combine(Path.GetTempPath(), $"instructions_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }

        [Fact]
        public async Task LoadAsync_WhenFileExists_ReturnsInstructionFile()
        {
            // Arrange
            var filePath = Path.Combine(_testDirectory, "AGENTS.md");
            var content = "# 测试指令\n\n这是一个测试指令文件。";
            await File.WriteAllTextAsync(filePath, content);

            // Act
            var result = await _loader.LoadAsync(filePath);

            // Assert
            result.Should().NotBeNull();
            result!.Path.Should().Be(filePath);
            result.Content.Should().Be(content);
            result.LastModified.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task LoadAsync_WhenFileNotExists_ReturnsNull()
        {
            // Arrange
            var filePath = Path.Combine(_testDirectory, "AGENTS.md");

            // Act
            var result = await _loader.LoadAsync(filePath);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task DiscoverAsync_FindsAgentsFilesInSearchOrder()
        {
            // Arrange - 创建嵌套目录结构
            var childDirectory = Path.Combine(_testDirectory, "child");
            Directory.CreateDirectory(childDirectory);

            // 当前目录的 AGENTS.md
            var currentFile = Path.Combine(childDirectory, "AGENTS.md");
            await File.WriteAllTextAsync(currentFile, "# 当前目录指令");

            // 父目录的 AGENTS.md
            var parentFile = Path.Combine(_testDirectory, "AGENTS.md");
            await File.WriteAllTextAsync(parentFile, "# 父目录指令");

            // Act
            var result = await _loader.DiscoverAsync(childDirectory);

            // Assert
            result.Should().HaveCount(2);
            result[0].Path.Should().Be(currentFile);
            result[0].Content.Should().Be("# 当前目录指令");
            result[1].Path.Should().Be(parentFile);
            result[1].Content.Should().Be("# 父目录指令");
        }

        [Fact]
        public void Merge_CombinesMultipleFilesWithSeparators()
        {
            // Arrange
            var files = new List<InstructionFile>
            {
                new InstructionFile { Path = "/path1/AGENTS.md", Content = "内容一" },
                new InstructionFile { Path = "/path2/AGENTS.md", Content = "内容二" }
            };

            // Act
            var result = _loader.Merge(files);

            // Assert
            result.Should().Contain("内容一");
            result.Should().Contain("内容二");
            result.Should().Contain("---");
            result.Should().Contain("来源: /path1/AGENTS.md");
            result.Should().Contain("来源: /path2/AGENTS.md");
        }
    }
}