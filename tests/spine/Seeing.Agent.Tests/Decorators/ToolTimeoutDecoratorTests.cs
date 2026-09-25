using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Decorators;
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

    /// <summary>捕获执行上下文的工具：用于断言装饰器透视执行时全字段透传。</summary>
    private sealed class CapturingTool : ITool
    {
        public string Id => "capture";
        public string Description => "捕获上下文";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolContext? Captured { get; private set; }

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            Captured = context;
            return Task.FromResult(new ToolResult { Success = true, Output = "ok" });
        }
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

    // C5 回归：超时装饰器重建 ToolContext 时必须全字段透传（尤其 PermissionAuthorizer），
    // 否则 MCP 授权门收到 null 授权器 → 安全绕过。
    [Fact]
    public async Task GlobalTimeout_ShouldForwardAllContextFields()
    {
        var capturing = new CapturingTool();
        var decorator = CreateDecorator(capturing, TimeSpan.FromSeconds(30));

        var authorizer = new Mock<IPermissionAuthorizer>().Object;
        var metadataSink = new Mock<IToolMetadataSink>().Object;
        var eventSink = new Mock<IToolEventSink>().Object;
        var services = new Mock<IServiceProvider>().Object;
        var agent = new AgentDefinition { Name = "plan" };
        var context = new ToolContext
        {
            SessionId = "s1",
            MessageId = "m1",
            CallId = "c1",
            Agent = agent,
            PermissionAuthorizer = authorizer,
            MetadataSink = metadataSink,
            EventSink = eventSink,
            Services = services,
            CancellationToken = CancellationToken.None
        };

        var result = await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, context);

        result.Success.Should().BeTrue();
        capturing.Captured.Should().NotBeNull();
        capturing.Captured!.SessionId.Should().Be("s1");
        capturing.Captured.MessageId.Should().Be("m1");
        capturing.Captured.CallId.Should().Be("c1");
        capturing.Captured.Agent.Should().BeSameAs(agent);
        capturing.Captured.PermissionAuthorizer.Should().BeSameAs(authorizer);
        capturing.Captured.MetadataSink.Should().BeSameAs(metadataSink);
        capturing.Captured.EventSink.Should().BeSameAs(eventSink);
        capturing.Captured.Services.Should().BeSameAs(services);
        capturing.Captured.CancellationToken.Should().NotBe(context.CancellationToken);
    }
}
