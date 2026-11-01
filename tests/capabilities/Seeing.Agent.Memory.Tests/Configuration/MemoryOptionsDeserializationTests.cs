using FluentAssertions;
using Seeing.Agent.Memory.Configuration;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

/// <summary>
/// <see cref="MemoryOptions"/> 反序列化兼容性。
/// <para>
/// <c>MemoryCostOptions</c> 已删除，但存量 <c>memory.json</c> 里可能残留 <c>Cost</c> 键。
/// STJ 默认忽略未知成员，删除后加载**不得**失败，且其余字段照常生效。
/// </para>
/// </summary>
public sealed class MemoryOptionsDeserializationTests
{
    [Fact]
    public void Deserialize_WithLegacyCostKey_ShouldNotThrow_AndIgnoreIt()
    {
        // Arrange：模拟存量 memory.json（含已删除的 Cost 节）
        const string legacyJson = """
        {
          "Capture": { "AutoCapture": false, "MaxSnippetChars": 2048 },
          "Extraction": { "ExtractEveryNTurns": 7 },
          "Cost": { "DailyTokenQuota": 500000, "RateLimitPerMinute": 30 }
        }
        """;

        // Act
        var options = JsonSerializer.Deserialize<MemoryOptions>(legacyJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert：不抛异常，已知字段生效，Cost 被静默忽略
        options.Should().NotBeNull();
        options!.Capture.AutoCapture.Should().BeFalse();
        options.Capture.MaxSnippetChars.Should().Be(2048);
        options.Extraction.ExtractEveryNTurns.Should().Be(7);
        typeof(MemoryOptions).GetProperty("Cost").Should().BeNull(
            "MemoryCostOptions 已删除：该配置项可写可落盘却永久无效，属用户可见的谎言");
    }

    [Fact]
    public void MemoryOptions_ShouldNotDefine_MemoryCostOptionsType()
    {
        var names = typeof(MemoryOptions).Assembly.GetTypes().Select(t => t.Name).ToArray();

        names.Should().NotContain("MemoryCostOptions");
    }
}
