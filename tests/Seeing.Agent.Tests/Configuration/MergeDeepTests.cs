using FluentAssertions;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class MergeDeepTests
{
    [Fact]
    public void Merge_PrimitiveTypes_OverrideWins()
    {
        // Arrange
        var baseObj = new TestConfig { Temperature = 0.5, MaxTokens = 100 };
        var overrideObj = new TestConfig { Temperature = 0.1 };

        // Act
        var result = MergeDeep.Merge(baseObj, overrideObj);

        // Assert
        result.Temperature.Should().Be(0.1);
        result.MaxTokens.Should().Be(100);
    }

    [Fact]
    public void Merge_NullOverride_UsesBase()
    {
        // Arrange
        var baseObj = new TestConfig { Temperature = 0.5, MaxTokens = 100 };

        // Act
        var result = MergeDeep.Merge(baseObj, null as TestConfig);

        // Assert
        result.Temperature.Should().Be(0.5);
        result.MaxTokens.Should().Be(100);
    }

    [Fact]
    public void Merge_NullBase_UsesOverride()
    {
        // Arrange
        var overrideObj = new TestConfig { Temperature = 0.1 };

        // Act
        var result = MergeDeep.Merge(null as TestConfig, overrideObj);

        // Assert
        result.Temperature.Should().Be(0.1);
    }

    [Fact]
    public void Merge_Dictionary_CombinesKeys()
    {
        // Arrange
        var baseObj = new DictionaryConfig
        {
            Options = new Dictionary<string, string>
            {
                ["key1"] = "base1",
                ["key2"] = "base2"
            }
        };
        var overrideObj = new DictionaryConfig
        {
            Options = new Dictionary<string, string>
            {
                ["key2"] = "override2",
                ["key3"] = "override3"
            }
        };

        // Act
        var result = MergeDeep.Merge(baseObj, overrideObj);

        // Assert
        result.Options["key1"].Should().Be("base1");  // 来自 base
        result.Options["key2"].Should().Be("override2");  // override 覆盖
        result.Options["key3"].Should().Be("override3");  // 来自 override
    }

    [Fact]
    public void MergeChain_MergesMultipleSources()
    {
        // Arrange
        var global = new TestConfig { Temperature = 0.5, MaxTokens = 100 };
        var project = new TestConfig { Temperature = 0.3 };
        var local = new TestConfig { MaxTokens = 200 };

        // Act
        var result = MergeDeep.MergeChain(global, project, local);

        // Assert
        result.Temperature.Should().Be(0.3);
        result.MaxTokens.Should().Be(200);
    }

    [Fact]
    public void Merge_NestedObject_MergesRecursively()
    {
        // Arrange
        var baseObj = new NestedConfig
        {
            Model = new ModelSettings { Name = "gpt-4", Version = "1" }
        };
        var overrideObj = new NestedConfig
        {
            Model = new ModelSettings { Version = "2" }
        };

        // Act
        var result = MergeDeep.Merge(baseObj, overrideObj);

        // Assert
        result.Model.Name.Should().Be("gpt-4");
        result.Model.Version.Should().Be("2");
    }

    // Test types
    private class TestConfig
    {
        public double Temperature { get; set; }
        public int? MaxTokens { get; set; }
    }

    private class DictionaryConfig
    {
        public Dictionary<string, string> Options { get; set; } = new();
    }

    private class NestedConfig
    {
        public ModelSettings Model { get; set; } = new();
    }

    private class ModelSettings
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }
}