using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 技能名称校验：不再强制「小写字母数字 + 连字符」。
/// <para>
/// 真实生态里的 SKILL.md 常见 `Self-Improving + Proactive Agent` 这类带大写 / 空格 / 加号的名称，
/// 旧规则会把它们整条判为加载失败（页面上表现为「技能加载问题」），而名称只是目录内的键。
/// </para>
/// </summary>
public sealed class SkillFileScannerNameTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-skill-name-{Guid.NewGuid():N}");

    public SkillFileScannerNameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort：临时目录清理失败不影响断言
        }
    }

    private void WriteSkill(string dirName, string frontmatterName)
    {
        var dir = Path.Combine(_root, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, SkillFileScanner.SkillFileName),
            $"""
             ---
             name: {frontmatterName}
             description: test skill
             ---

             body
             """);
    }

    [Fact]
    public async Task ScanAsync_NameWithUppercaseSpacesAndPlus_ShouldLoad()
    {
        WriteSkill("self-improving", "Self-Improving + Proactive Agent");

        var result = await SkillFileScanner.ScanAsync(
            [new SkillRoot(_root, SkillSource.UserFile)],
            NullLogger.Instance);

        result.Issues.Should().BeEmpty("带大写 / 空格 / 加号的名称不应再被判为加载失败");
        result.Entries.Should().ContainSingle();
        result.Entries[0].Info.Name.Should().Be("Self-Improving + Proactive Agent");
    }

    [Theory]
    [InlineData("my_skill")]
    [InlineData("skill.v2")]
    [InlineData("MixedCase-Skill")]
    public async Task ScanAsync_OtherNameShapes_ShouldLoad(string name)
    {
        WriteSkill("dir", name);

        var result = await SkillFileScanner.ScanAsync(
            [new SkillRoot(_root, SkillSource.UserFile)],
            NullLogger.Instance);

        result.Issues.Should().BeEmpty();
        result.Entries.Should().ContainSingle().Which.Info.Name.Should().Be(name);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("has/slash")]
    [InlineData("has:colon")]
    public async Task ScanAsync_PathLikeName_ShouldStillBeRejected(string name)
    {
        WriteSkill("dir", name);

        var result = await SkillFileScanner.ScanAsync(
            [new SkillRoot(_root, SkillSource.UserFile)],
            NullLogger.Instance);

        result.Entries.Should().BeEmpty("名称会被当作键使用，不能含路径分隔符等字符");
        result.Issues.Should().ContainSingle();
    }
}
