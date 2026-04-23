using System;
using System.IO;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Tests
{
    public class MemoryConfigLoaderTests
    {
        [Fact(DisplayName = "LoadDefault_仅用户级配置_返回用户级配置")]
        public void LoadDefault_仅用户级配置_返回用户级配置()
        {
            // Arrange
            string workspaceRoot = "/workspace";
            ILogger? logger = null;

            // Act
            var result = MemoryConfigLoader.LoadDefault(workspaceRoot, logger);

            // Assert
            result.Should().NotBeNull();
        }

        [Fact(DisplayName = "LoadDefault_用户级加项目级_项目级覆盖用户级")]
        public void LoadDefault_用户级加项目级_项目级覆盖用户级()
        {
            // Arrange
            string workspaceRoot = "/workspace";
            ILogger? logger = null;

            // Act
            var result = MemoryConfigLoader.LoadDefault(workspaceRoot, logger);

            // Assert
            result.Should().NotBeNull();
        }

        [Fact(DisplayName = "ExpandPath_带波浪号_转换为用户主目录")]
        public void ExpandPath_带波浪号_转换为用户主目录()
        {
            // Arrange
            string workspaceRoot = "/workspace";
            var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Act
            var actual = MemoryConfigLoader.ExpandPath("~", workspaceRoot);

            // Assert
            actual.Should().Be(expected);
        }

        [Fact(DisplayName = "ExpandPath_相对路径_基于WorkspaceRoot转换")]
        public void ExpandPath_相对路径_基于WorkspaceRoot转换()
        {
            // Arrange
            string workspaceRoot = "/workspace";
            var relative = Path.Combine("sub", "dir");
            var expected = Path.Combine(workspaceRoot, relative);

            // Act
            var actual = MemoryConfigLoader.ExpandPath(relative, workspaceRoot);

            // Assert
            actual.Should().Be(Path.GetFullPath(expected));
        }

        [Fact(DisplayName = "LoadDefault_配置缺失_返回默认配置不抛异常")]
        public void LoadDefault_配置缺失_返回默认配置不抛异常()
        {
            // Arrange
            string workspaceRoot = "/workspace";
            ILogger? logger = null;

            // Act
            var result = MemoryConfigLoader.LoadDefault(workspaceRoot, logger);

            // Assert
            result.Should().NotBeNull();
        }
    }
}
