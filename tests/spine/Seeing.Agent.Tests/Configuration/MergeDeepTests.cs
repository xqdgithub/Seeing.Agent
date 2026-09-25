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
        var local = new TestConfig { Temperature = 0.25, MaxTokens = 200 };

        // Act
        var result = MergeDeep.MergeChain(global, project, local);

        // Assert
        result.Temperature.Should().Be(0.25);
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

    [Fact]
    public void Merge_BoolFalse_OverrideWins()
    {
        var baseObj = new BoolConfig { Enabled = true };
        var overrideObj = new BoolConfig { Enabled = false };

        var result = MergeDeep.Merge(baseObj, overrideObj);

        result.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Merge_NumericZero_DoesNotOverride_UserValue()
    {
        // 撤销“数值 0 始终覆盖”：反序列化无法区分“未写”与“写 0”，
        // 故数值 0 / TimeSpan.Zero 恢复“等于默认值即视为未设置”的原有分层语义。
        var baseObj = new NumericConfig { Count = 5, Ratio = 1.5, Interval = TimeSpan.FromSeconds(30) };
        var overrideObj = new NumericConfig { Count = 0, Ratio = 0, Interval = TimeSpan.Zero };

        var result = MergeDeep.Merge(baseObj, overrideObj);

        result.Count.Should().Be(5);
        result.Ratio.Should().Be(1.5);
        result.Interval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Merge_NumericNonZero_StillOverrides()
    {
        var baseObj = new NumericConfig { Count = 5, Ratio = 1.5, Interval = TimeSpan.FromSeconds(30) };
        var overrideObj = new NumericConfig { Count = 7, Ratio = 2.5, Interval = TimeSpan.FromSeconds(10) };

        var result = MergeDeep.Merge(baseObj, overrideObj);

        result.Count.Should().Be(7);
        result.Ratio.Should().Be(2.5);
        result.Interval.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Merge_NullableZero_DoesNotOverride()
    {
        // 可空数值 0 同样视为“未设置”，保留用户级非零值；null 已由 null 分支处理。
        var baseObj = new TestConfig { MaxTokens = 100 };
        var overrideObj = new TestConfig { MaxTokens = 0 };

        var result = MergeDeep.Merge(baseObj, overrideObj);

        result.MaxTokens.Should().Be(100);
    }

    [Fact]
    public void Merge_NullOverride_NestedObject_IsDeepCopied()
    {
        // 项目级未设置嵌套对象（null）时，结果不得与用户级来源共享引用
        var userSource = new NullableNestedConfig
        {
            Model = new ModelSettings { Name = "gpt-4", Version = "1" }
        };
        var projectSource = new NullableNestedConfig { Model = null };

        var result = MergeDeep.Merge(userSource, projectSource);

        result.Model.Should().NotBeNull();
        result.Model.Should().NotBeSameAs(userSource.Model);
        result.Model!.Name = "mutated";
        userSource.Model!.Name.Should().Be("gpt-4");
    }

    [Fact]
    public void Merge_NullOverrideTopLevel_ReturnsDeepCopy()
    {
        var userSource = new NullableNestedConfig
        {
            Model = new ModelSettings { Name = "gpt-4", Version = "1" }
        };

        var result = MergeDeep.Merge(userSource, null as NullableNestedConfig);

        result.Should().NotBeSameAs(userSource);
        result.Model.Should().NotBeSameAs(userSource.Model);
        result.Model!.Version = "mutated";
        userSource.Model!.Version.Should().Be("1");
    }

    [Fact]
    public void Merge_Dictionary_DeepMergesNestedObjects()
    {
        var baseObj = new NestedDictionaryConfig
        {
            Backends = new Dictionary<string, BackendConfig>
            {
                ["cursor"] = new() { Command = "old.cmd", Args = new List<string> { "acp" } }
            }
        };
        var overrideObj = new NestedDictionaryConfig
        {
            Backends = new Dictionary<string, BackendConfig>
            {
                ["cursor"] = new() { Command = "new.cmd" }
            }
        };

        var result = MergeDeep.Merge(baseObj, overrideObj);

        result.Backends["cursor"].Command.Should().Be("new.cmd");
        result.Backends["cursor"].Args.Should().Equal("acp");
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

    private class BoolConfig
    {
        public bool Enabled { get; set; }
    }

    private class NumericConfig
    {
        public int Count { get; set; }
        public double Ratio { get; set; }
        public TimeSpan Interval { get; set; }
    }

    private class NullableNestedConfig
    {
        public ModelSettings? Model { get; set; }
    }

    private class BackendConfig
    {
        public string Command { get; set; } = string.Empty;
        public List<string> Args { get; set; } = new();
    }

    private class NestedDictionaryConfig
    {
        public Dictionary<string, BackendConfig> Backends { get; set; } = new();
    }

    private class ModelSettings
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }
}