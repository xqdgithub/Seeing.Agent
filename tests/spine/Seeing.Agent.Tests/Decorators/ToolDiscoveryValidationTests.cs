using FluentAssertions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.Discovery;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

/// <summary>
/// ToolDiscovery 非法签名拒绝 + required 语义对齐（批次 4 F）。
/// </summary>
public class ToolDiscoveryValidationTests
{
    [Fact]
    public void DiscoverTools_InvalidSignatures_ShouldBeSkippedWithWarning()
    {
        var logger = new ListLogger();

        var tools = ToolDiscovery.DiscoverTools(typeof(InvalidSignatureTools), logger);

        tools.Select(t => t.Id).Should().BeEquivalentTo(new[] { "Valid" });
        logger.Warnings.Should().Contain(w => w.Contains("AsyncVoid"));
        logger.Warnings.Should().Contain(w => w.Contains("OutParam"));
        logger.Warnings.Should().Contain(w => w.Contains("RefParam"));
        logger.Warnings.Should().Contain(w => w.Contains("Generic"));
    }

    [Fact]
    public void DiscoverTools_RequiredSemantics_ShouldMatchExecutionTimeJudgement()
    {
        var tools = ToolDiscovery.DiscoverTools(typeof(RequiredSemanticsTools));
        var tool = tools.Single(t => t.Id == "RequiredSemantics");

        var required = tool.ParametersSchema.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        // 值类型无默认值 → 必需；显式 [Required] → 必需
        required.Should().Contain("count");
        required.Should().Contain("explicitRequired");
        // 引用类型 / 可空类型无明显 [Required] → 非必需；有默认值 → 非必需
        required.Should().NotContain("referenceWithoutDefault");
        required.Should().NotContain("nullableWithoutDefault");
        required.Should().NotContain("withDefault");
    }

    // ===== 测试桩 =====

    private static class InvalidSignatureTools
    {
        [Tool("async void 不受支持")]
        public static async void AsyncVoid()
        {
            await Task.Yield();
        }

        [Tool("out 参数不受支持")]
        public static Task OutParam(out int value)
        {
            value = 0;
            return Task.CompletedTask;
        }

        [Tool("ref 参数不受支持")]
        public static Task RefParam(ref int value)
        {
            return Task.CompletedTask;
        }

        [Tool("泛型方法不受支持")]
        public static Task Generic<T>(T value) => Task.CompletedTask;

        [Tool("合法工具")]
        public static Task<string> Valid(string input) => Task.FromResult(input);
    }

    private static class RequiredSemanticsTools
    {
        [Tool("required 语义对照")]
        public static Task<string> RequiredSemantics(
            int count,
            [Required] string explicitRequired,
            string referenceWithoutDefault,
            int? nullableWithoutDefault,
            string withDefault = "d")
            => Task.FromResult("ok");
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
