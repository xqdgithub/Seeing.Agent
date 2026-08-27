using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Decorators;
using Seeing.Agent.Helpers;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

/// <summary>
/// ToolCapabilityReader 能力读取辅助测试
/// <para>验证：缺失→默认值；"true" 大小写；装饰器解包读到最内层声明；[ToolCapability] Attribute → 字典。</para>
/// </summary>
public class ToolCapabilityReaderTests
{
    private sealed class CachingTool : ToolBase
    {
        public CachingTool() : base(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { }
        public override string Id => "cacheable";
        public override string Description => "test";
        public override IReadOnlyDictionary<string, string>? Capabilities => new Dictionary<string, string>
        {
            [ToolCapabilityKeys.CacheEnabled] = "true",
            [ToolCapabilityKeys.CacheScope] = "global"
        };
        public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true });
    }

    [ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
    private sealed class AttributedTool : ITool
    {
        public string Id => "attributed";
        public string Description => "test";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true });
    }

    private sealed class PlainTool : ITool
    {
        public string Id => "plain";
        public string Description => "test";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true });
    }

    [Fact]
    public void GetBool_MissingKey_ShouldReturnDefault()
    {
        ToolCapabilityReader.GetBool(new PlainTool(), ToolCapabilityKeys.CacheEnabled).Should().BeFalse();
        ToolCapabilityReader.GetBool(new PlainTool(), ToolCapabilityKeys.TimeoutSkip).Should().BeFalse();
        ToolCapabilityReader.GetBool(new PlainTool(), ToolCapabilityKeys.CacheEnabled, defaultValue: true).Should().BeTrue();
    }

    [Fact]
    public void GetBool_TrueValue_ShouldParseCaseInsensitive()
    {
        var tool = new CachingTool();
        ToolCapabilityReader.GetBool(tool, ToolCapabilityKeys.CacheEnabled).Should().BeTrue();
        ToolCapabilityReader.GetBool(tool, ToolCapabilityKeys.TimeoutSkip).Should().BeFalse();
    }

    [Fact]
    public void GetDurationMs_InvalidOrMissing_ShouldReturnNull()
    {
        ToolCapabilityReader.GetDurationMs(new PlainTool(), ToolCapabilityKeys.CacheTtl).Should().BeNull();
        ToolCapabilityReader.GetDurationMs(new CachingTool(), ToolCapabilityKeys.CacheTtl).Should().BeNull();
    }

    [Fact]
    public void GetDurationMs_ValidMs_ShouldParse()
    {
        var tool = new ToolBase2 { };
        ToolCapabilityReader.GetDurationMs(tool, ToolCapabilityKeys.CacheTtl).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void GetCacheScope_DefaultSession()
    {
        ToolCapabilityReader.GetCacheScope(new PlainTool()).Should().Be("session");
        ToolCapabilityReader.GetCacheScope(new CachingTool()).Should().Be("global");
    }

    [Fact]
    public void Attribute_ShouldBeReadableByReader()
    {
        var tool = new AttributedTool();
        ToolCapabilityReader.GetBool(tool, ToolCapabilityKeys.TimeoutSkip).Should().BeTrue();
        ToolCapabilityReader.Get(tool, ToolCapabilityKeys.TimeoutSkip).Should().Be("true");
    }

    [Fact]
    public void Decorator_ShouldPassThroughInnerCapabilities()
    {
        var inner = new CachingTool();
        var wrapped = new RetryToolDecorator(inner, maxRetries: 2);

        ToolCapabilityReader.GetBool(wrapped, ToolCapabilityKeys.CacheEnabled).Should().BeTrue();
        ToolCapabilityReader.GetCacheScope(wrapped).Should().Be("global");
    }

    [Fact]
    public void Decorator_UnimplementedInner_ShouldDefault()
    {
        var wrapped = new RetryToolDecorator(new PlainTool(), maxRetries: 2);
        ToolCapabilityReader.GetBool(wrapped, ToolCapabilityKeys.CacheEnabled).Should().BeFalse();
    }

    [Fact]
    public void Innermost_NonDecorator_ShouldReturnSame()
    {
        var tool = new PlainTool();
        ToolCapabilityReader.Innermost(tool).Should().BeSameAs(tool);
    }

    private sealed class ToolBase2 : ToolBase
    {
        public ToolBase2() : base(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { }
        public override string Id => "ttl";
        public override string Description => "test";
        public override IReadOnlyDictionary<string, string>? Capabilities => new Dictionary<string, string>
        {
            [ToolCapabilityKeys.CacheEnabled] = "true",
            [ToolCapabilityKeys.CacheTtl] = "30000"
        };
        public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true });
    }
}
