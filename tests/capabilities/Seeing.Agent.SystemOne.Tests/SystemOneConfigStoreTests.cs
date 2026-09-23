using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneConfigStoreTests
{
    [Fact]
    public void 常量_应为约定值()
    {
        SystemOneConfigStore.SectionName.Should().Be("SystemOne");
        SystemOneConfigStore.FileName.Should().Be("systemone.json");
    }

    [Fact]
    public void SectionMeta_应声明UserOnly与字典类型()
    {
        var meta = SystemOneConfigStore.SectionMeta;

        meta.Key.Should().Be(SystemOneConfigStore.SectionName);
        meta.FileName.Should().Be(SystemOneConfigStore.FileName);
        meta.Scope.Should().Be(ConfigScope.UserOnly);
        meta.SectionType.Should().Be(typeof(Dictionary<string, SystemOneProviderConfig>));
    }

    [Fact]
    public void Read_存在配置_应返回同一字典()
    {
        var expected = new Dictionary<string, SystemOneProviderConfig>
        {
            ["typesafe"] = new()
            {
                Id = "typesafe",
                Type = SystemOneProviderTypes.TypeSafe,
                ApiKey = "k"
            }
        };
        var sut = new SystemOneConfigStore(new TestConfigSectionStore(expected));

        sut.Read().Should().BeSameAs(expected);
    }

    [Fact]
    public void Read_缺失配置_应返回空字典()
    {
        var sut = new SystemOneConfigStore(new TestConfigSectionStore());

        var result = sut.Read();

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void 构造_空存储_应抛ArgumentNullException()
    {
        var act = () => new SystemOneConfigStore(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
