using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 只读门面锁定：<see cref="ISkillManager"/> 不得持有独立真相，必须投影 <see cref="ISkillCatalog"/>。
/// </summary>
public sealed class SkillManagerProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"seeing-skill-facade-{Guid.NewGuid():N}");

    public SkillManagerProjectionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    [Fact]
    public async Task Facade_ShouldReflectCatalogRefresh_WithoutAnyOtherSync()
    {
        // Arrange：目录已就绪；门面除了目录之外没有任何自有数据源
        var catalog = new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance);
        catalog.SetRoots([Path.Combine(_root, "skills")]);
        var manager = new SkillManager(NullLogger<SkillManager>.Instance, catalog);
        ISkillManager facade = manager;

        // 初始：没有任何技能
        facade.GetAllSkillInfos().Should().BeEmpty();

        // Act：只让目录刷新（不经过命令 / UI / 门面刷新）
        var skillDirectory = Path.Combine(_root, "skills", "demo");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(skillDirectory, "SKILL.md"),
            """
            ---
            name: demo
            description: 演示技能
            ---

            正文
            """,
            TestContext.Current.CancellationToken);

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert：门面立刻反映目录快照
        facade.GetAllSkillInfos().Should().ContainKey("demo");
        facade.GetSkillInfo("demo").Should().NotBeNull();
        facade.GetSkillInfo("demo")!.Description.Should().Be("演示技能");
        manager.GetAllSkillInfosIncludingDisabled().Should().ContainKey("demo");
    }
}
