using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>SkillManager 注册/注销 API 回归测试（门面转发到技能目录）。</summary>
public class SkillManagerUnregisterTests
{
    private static SkillManager CreateManager() => new(
        NullLogger<SkillManager>.Instance,
        new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance));

    [Fact]
    public void Unregister_已注册技能_应移除且返回true()
    {
        var manager = CreateManager();
        manager.RegisterEmbeddedSkill(new SkillInfo
        {
            Name = "demo-skill",
            Description = "演示技能",
            Content = "正文"
        });
        manager.GetSkillInfo("demo-skill").Should().NotBeNull();

        var removed = manager.Unregister("demo-skill");

        removed.Should().BeTrue();
        manager.GetSkillInfo("demo-skill").Should().BeNull();
        manager.GetAllSkillInfos().Should().NotContainKey("demo-skill");
    }

    [Fact]
    public void Unregister_未注册技能_应返回false()
    {
        var manager = CreateManager();

        var removed = manager.Unregister("missing-skill");

        removed.Should().BeFalse();
    }

    [Fact]
    public void Unregister_空名称_应返回false()
    {
        var manager = CreateManager();

        manager.Unregister(string.Empty).Should().BeFalse();
    }
}
