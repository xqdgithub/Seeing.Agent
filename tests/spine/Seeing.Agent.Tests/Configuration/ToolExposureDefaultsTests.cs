using FluentAssertions;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// 钉选配置回归防护：默认集在代码（Hosting），配置初始化器必须为空，
/// 否则 MergeDeep 会把"未设置"误判为"项目级已设置"而覆盖用户级配置。
/// </summary>
public class ToolExposureDefaultsTests
{
    [Fact]
    public void OptionsPinnedTools_Default_ShouldBeEmpty_ToAvoidMergeOverride()
    {
        new SeeingAgentOptions().Tools.Exposure.PinnedTools.Should().BeEmpty();
    }

    [Fact]
    public void Merge_UserPinnedWithDefaultProject_ShouldKeepUserValue()
    {
        var user = new SeeingAgentOptions();
        user.Tools.Exposure.PinnedTools = ["custom"];

        var merged = MergeDeep.Merge(user, new SeeingAgentOptions());

        merged.Tools.Exposure.PinnedTools.Should().Equal("custom");
    }
}
