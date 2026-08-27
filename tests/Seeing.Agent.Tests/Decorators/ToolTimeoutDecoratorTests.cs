using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Decorators;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

/// <summary>
/// ToolTimeoutDecorator 测试 - 工具执行漏斗内全局兜底超时
/// <para>
/// 验证：未声明能力工具被全局兜底超时；timeout.skip=true 豁免；timeout.budget 按工具上限触发；
/// 超时结果为 Failure + Title="执行超时" + Metadata["timeout"]=true；外层取消不被误判为超时。
/// </para>
/// </summary>
public class ToolTimeoutDecoratorTests
{
    /// <summary>挂起工具：直到取消令牌触发抛 OCE，模拟无内部超时的第三方工具。</summary>
    private class HangingTool : ITool
    {
        public string Id { get; set; } = "hang";
        public string Description => "挂起工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), context.CancellationToken);
            return new ToolResult { Success = true, Output = "unreachable" };
        }
    }

    /// <summary>声明 timeout.skip=true 的挂起工具：应豁免全局兜底超时。</summary>
    [ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
    private sealed class TimeoutSkipHangingTool : HangingTool
    {
    }

    /// <summary>声明 timeout.budget=300ms 的挂起工具：应按工具自身上限触发，而非全局。</summary>
    [ToolCapability(ToolCapabilityKeys.TimeoutBudget, "300")]
    private sealed class BudgetHangingTool : HangingTool
    {
    }

    /// <summary>立即返回成功结果的工具：不应被超时误杀。</summary>
    private sealed class FastTool : ITool
    {
        public string Id => "fast";
        public string Description => "快速工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true, Output = "ok" });
    }

    /// <summary>构造装饰器，全局兜底超时 = globalTimeout。</summary>
    private static ToolTimeoutDecorator CreateDecorator(ITool inner, TimeSpan? globalTimeout)
    {
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue)
            .Returns(new SeeingAgentOptions { ToolExecutionTimeout = globalTimeout });

        return new ToolTimeoutDecorator(
            inner,
            options.Object,
            NullLogger<ToolTimeoutDecorator>.Instance);
    }

    [Fact]
    public async Task UndeclaredTool_ShouldBeKilledByGlobalTimeout()
    {
        var decorator = CreateDecorator(new HangingTool(), TimeSpan.FromMilliseconds(200));
        var result = await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, new ToolContext { CancellationToken = CancellationToken.None });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("执行超时");
        result.Error.Should().Contain("超时");
        result.Metadata.Should().ContainKey("timeout").WhoseValue.Should().Be(true);
    }

    [Fact]
    public async Task TimeoutSkipTool_ShouldSurviveGlobalTimeout()
    {
        // 豁免工具：内部挂 10 分钟，但外层取消令牌在 500ms 触发 → 应得到 Cancelled（外层取消），而非"执行超时"
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var decorator = CreateDecorator(new TimeoutSkipHangingTool(), TimeSpan.FromMilliseconds(200));

        var ex = await Record.ExceptionAsync(() => decorator.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement,
            new ToolContext { CancellationToken = cts.Token }));

        ex.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task BudgetTool_ShouldTimeoutByToolBudget_NotGlobal()
    {
        // 全局超时未开启（null），仅工具自身 timeout.budget=300ms 生效
        var decorator = CreateDecorator(new BudgetHangingTool(), null);
        var result = await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, new ToolContext { CancellationToken = CancellationToken.None });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("执行超时");
        result.Error.Should().Contain("超时");
    }

    [Fact]
    public async Task NoGlobalTimeout_AndNoBudget_ShouldRunToCompletion()
    {
        var decorator = CreateDecorator(new FastTool(), null);
        var result = await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, new ToolContext { CancellationToken = CancellationToken.None });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("ok");
    }

    [Fact]
    public async Task FastTool_UnderGlobalTimeout_ShouldNotBeKilled()
    {
        var decorator = CreateDecorator(new FastTool(), TimeSpan.FromSeconds(30));
        var result = await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, new ToolContext { CancellationToken = CancellationToken.None });

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task OuterCancellation_ShouldThrow_NotReportTimeout()
    {
        // 外层取消先于超时触发：应抛 OCE（取消优先），而非返回"执行超时"结果
        using var cts = new CancellationTokenSource();
        var decorator = CreateDecorator(new HangingTool(), TimeSpan.FromMinutes(5));

        var task = decorator.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement,
            new ToolContext { CancellationToken = cts.Token });
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => task);
        ex.Should().BeAssignableTo<OperationCanceledException>();
    }
}
