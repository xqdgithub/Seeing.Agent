using Seeing.Agent.Abstractions.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Decorators;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

public class ToolDecoratorRegistryTests
{
    [Fact]
    public void Apply_RegistrationOrder_LastRegisteredIsOutermost()
    {
        // 链序契约：后注册者位于最外层（Apply 按注册顺序依次包裹）
        var registry = new ToolDecoratorRegistry();
        registry.Register(t => new StubToolDecorator(t, "first"));
        registry.Register(t => new StubToolDecorator(t, "second"));
        var tool = new StubTool();

        var result = (StubToolDecorator)registry.Apply(tool);

        result.Tag.Should().Be("second");
        var middle = (StubToolDecorator)result.Unwrap();
        middle.Tag.Should().Be("first");
        middle.Unwrap().Should().BeSameAs(tool);
    }

    [Fact]
    public void Apply_TimeoutRegisteredAfterRetry_TimeoutWrapsRetry()
    {
        // 决策语义：Timeout(Retry(tool)) —— 全局超时覆盖整次工具调用的重试总时长
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new SeeingAgentOptions());

        var registry = new ToolDecoratorRegistry();
        registry.Register(t => new RetryToolDecorator(t, maxRetries: 3, delay: TimeSpan.Zero));
        registry.Register(t => new ToolTimeoutDecorator(t, options.Object, NullLogger.Instance));

        var tool = new StubTool();
        var applied = registry.Apply(tool);

        applied.Should().BeOfType<ToolTimeoutDecorator>();
        var timeout = (ToolTimeoutDecorator)applied;
        var retry = (RetryToolDecorator)timeout.Unwrap();
        retry.Unwrap().Should().BeSameAs(tool);
    }

    [Fact]
    public void Apply_NoDecorators_ShouldReturnOriginalTool()
    {
        var registry = new ToolDecoratorRegistry();
        var tool = new StubTool();
        var result = registry.Apply(tool);

        result.Should().BeSameAs(tool);
    }

    [Fact]
    public void Apply_SingleDecorator_ShouldWrapTool()
    {
        var registry = new ToolDecoratorRegistry();
        registry.Register(t => new StubToolDecorator(t));
        var tool = new StubTool();
        var result = registry.Apply(tool);

        result.Should().BeOfType<StubToolDecorator>();
        var decorator = (StubToolDecorator)result;
        Utils.Unwrap(decorator).Should().BeSameAs(tool);
    }

    [Fact]
    public void Apply_MultipleDecorators_ShouldChainInOrder()
    {
        var registry = new ToolDecoratorRegistry();
        registry.Register(t => new StubToolDecorator(t, "outer"));
        registry.Register(t => new StubToolDecorator(t, "inner"));
        var tool = new StubTool();
        var result = registry.Apply(tool);

        result.Should().BeOfType<StubToolDecorator>();
        var outer = (StubToolDecorator)result;
        outer.Tag.Should().Be("inner");
        var inner = (StubToolDecorator)Utils.Unwrap(outer);
        inner.Tag.Should().Be("outer");
    }

    [Fact]
    public void Count_ShouldReflectRegisteredDecorators()
    {
        var registry = new ToolDecoratorRegistry();
        registry.Register(t => new StubToolDecorator(t));

        registry.Count.Should().Be(1);
    }

    [Fact]
    public void Apply_NullTool_ShouldThrow()
    {
        var registry = new ToolDecoratorRegistry();
        var act = () => registry.Apply(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_NullFactory_ShouldThrow()
    {
        var registry = new ToolDecoratorRegistry();
        var act = () => registry.Register(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Decorator_PropagatesId_FromInner()
    {
        var registry = new ToolDecoratorRegistry();
        registry.Register(t => new StubToolDecorator(t));
        var tool = new StubTool();
        var result = registry.Apply(tool);

        result.Id.Should().Be("stub");
    }

    [Fact]
    public void Apply_ReturnsTool_WhenFactoryReturnsSameTool()
    {
        var registry = new ToolDecoratorRegistry();
        registry.Register(t => t);
        var tool = new StubTool();
        var result = registry.Apply(tool);

        result.Should().BeSameAs(tool);
    }

    // ===== 测试桩 =====

    private class StubTool : ITool
    {
        public string Id => "stub";
        public string Description => "stub tool";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => default;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
            => Task.FromResult(new ToolResult { Success = true });
    }

    private class StubToolDecorator : ToolDecorator
    {
        private readonly string _tag;
        public string Tag => _tag;

        public StubToolDecorator(ITool inner, string tag = "") : base(inner)
        {
            _tag = tag;
        }
    }

    private static class Utils
    {
        public static ITool Unwrap(ToolDecorator decorator)
        {
            return decorator.Unwrap();
        }
    }
}
