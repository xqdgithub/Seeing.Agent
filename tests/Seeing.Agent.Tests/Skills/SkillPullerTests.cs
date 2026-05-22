using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Skills;
using Seeing.Agent.Skills.Pulling;
using Xunit;

namespace Seeing.Agent.Tests.Skills;

public class SkillPullerTests
{
    [Fact]
    public async Task PullFromLocalAsync_ExistingFile_ShouldSucceed()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        var content = "---\nname: test-skill\ndescription: Test\n---\n# Test Skill";
        await File.WriteAllTextAsync(tempFile, content);

        var logger = new Mock<ILogger<SkillPuller>>();
        var puller = new SkillPuller(logger.Object, Path.GetTempPath());

        // Act
        var result = await puller.PullFromLocalAsync(tempFile);

        // Assert
        result.Success.Should().BeTrue();
        result.SkillName.Should().Be("Test Skill"); // Extracted from "# Test Skill" heading

        // Cleanup
        File.Delete(tempFile);
    }

    [Fact]
    public async Task PullFromLocalAsync_NonExistentFile_ShouldFail()
    {
        // Arrange
        var logger = new Mock<ILogger<SkillPuller>>();
        var puller = new SkillPuller(logger.Object);

        // Act
        var result = await puller.PullFromLocalAsync("/non/existent/path.md");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task ValidateSource_LocalPath_ShouldValidate()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        var logger = new Mock<ILogger<SkillPuller>>();
        var puller = new SkillPuller(logger.Object);

        // Act
        var result = await puller.ValidateSourceAsync(tempFile);

        // Assert
        result.IsValid.Should().BeTrue();

        // Cleanup
        File.Delete(tempFile);
    }

    [Fact]
    public async Task ValidateSource_InvalidPath_ShouldFail()
    {
        // Arrange
        var logger = new Mock<ILogger<SkillPuller>>();
        var puller = new SkillPuller(logger.Object);

        // Act
        var result = await puller.ValidateSourceAsync("not-a-valid-source");

        // Assert
        result.IsValid.Should().BeFalse();
    }
}

public class BuiltinSkillLoaderTests
{
    [Fact]
    public async Task LoadAllAsync_ShouldReturnDefaultSkills()
    {
        // Arrange
        var logger = new Mock<ILogger<BuiltinSkillLoader>>();
        var loader = new BuiltinSkillLoader(logger.Object, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        // Act
        var skills = await loader.LoadAllAsync();

        // Assert
        skills.Should().NotBeEmpty();
        skills.Should().Contain(s => s.Name == "code-review");
        skills.Should().Contain(s => s.Name == "test-generator");
        skills.Should().Contain(s => s.Name == "documentation");
    }

    [Fact]
    public async Task GetAsync_ExistingSkill_ShouldReturn()
    {
        // Arrange
        var logger = new Mock<ILogger<BuiltinSkillLoader>>();
        var loader = new BuiltinSkillLoader(logger.Object, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await loader.LoadAllAsync();

        // Act
        var skill = await loader.GetAsync("code-review");

        // Assert
        skill.Should().NotBeNull();
        skill!.Name.Should().Be("code-review");
        skill.Tags.Should().Contain("code");
    }
}

public class SkillPermissionFilterTests
{
    [Fact]
    public void ValidateContent_SafeContent_ShouldPass()
    {
        // Arrange
        var logger = new Mock<ILogger<SkillPermissionFilter>>();
        var filter = new SkillPermissionFilter(logger.Object, null);
        var content = "---\nname: safe-skill\n---\n# Safe Skill\nThis is safe content.";

        // Act
        var result = filter.ValidateContent(content);

        // Assert
        result.IsSecure.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateContent_DangerousPattern_ShouldWarn()
    {
        // Arrange
        var logger = new Mock<ILogger<SkillPermissionFilter>>();
        var filter = new SkillPermissionFilter(logger.Object, null);
        var content = "This skill uses eval() for dynamic execution.";

        // Act
        var result = filter.ValidateContent(content);

        // Assert
        result.Warnings.Should().NotBeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("eval"));
    }

    [Fact]
    public void ValidateContent_HardcodedSecret_ShouldFail()
    {
        // Arrange
        var logger = new Mock<ILogger<SkillPermissionFilter>>();
        var filter = new SkillPermissionFilter(logger.Object, null);
        var content = "password = \"hardcoded-password-123\"";

        // Act
        var result = filter.ValidateContent(content);

        // Assert
        result.IsSecure.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("password"));
    }
}
