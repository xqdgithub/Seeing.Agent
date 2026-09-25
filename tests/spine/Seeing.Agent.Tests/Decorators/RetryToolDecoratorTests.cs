using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Decorators;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

/// <summary>
/// RetryToolDecorator 直接测试 — 重试次数、退避时长与异常白名单。
/// <para>
/// 锁定语义：总尝试次数 = maxRetries；退避为线性增长（delay × (attempt + 1)）；
/// 可重试异常白名单 = TimeoutException / HttpRequestException / IOException / TaskCanceledException（未取消）；
/// 非白名单异常立即抛出；白名单异常在**最后一次尝试**不再被捕获而向上抛出；
/// <c>Metadata["retryable"]=true</c> 的失败结果会耗尽重试后返回“重试耗尽”。
/// </para>
/// </summary>
public class RetryToolDecoratorTests
{
    /// <summary>可编程替身：按队列抛出异常或返回结果，并记录调用次数。</summary>
    private sealed class ScriptedTool : ITool
    {
        private readonly Queue<Func<ToolResult>> _script = new();

        public int CallCount { get; private set; }
        public string Id => "scripted";
        public string Description => "脚本化工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public ScriptedTool Throw(Exception ex)
        {
            _script.Enqueue(() => throw ex);
            return this;
        }

        public ScriptedTool Return(ToolResult result)
        {
            _script.Enqueue(() => result);
            return this;
        }

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            CallCount++;
            if (_script.Count == 0)
                return Task.FromResult(ToolResult.Succeeded("default"));

            var next = _script.Dequeue();
            return Task.FromResult(next());
        }
    }

    private static ToolContext Context() => new() { CancellationToken = CancellationToken.None };

    private static JsonElement Args() => JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task 首次成功_不应重试()
    {
        var tool = new ScriptedTool().Return(ToolResult.Succeeded("ok"));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var result = await decorator.ExecuteAsync(Args(), Context());

        result.Success.Should().BeTrue();
        result.Output.Should().Be("ok");
        tool.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task 可重试异常_应尝试最大次数_最后一次尝试后向上抛出()
    {
        var tool = new ScriptedTool()
            .Throw(new TimeoutException("t1"))
            .Throw(new TimeoutException("t2"))
            .Throw(new TimeoutException("t3"));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var act = async () => await decorator.ExecuteAsync(Args(), Context());

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("t3");
        tool.CallCount.Should().Be(3, "maxRetries=3 表示总共 3 次尝试");
    }

    [Fact]
    public async Task 全部尝试返回可重试失败_应返回重试耗尽()
    {
        var retryable = () => new ToolResult
        {
            Success = false,
            Error = "temporary",
            Metadata = new Dictionary<string, object> { ["retryable"] = true }
        };
        var tool = new ScriptedTool().Return(retryable()).Return(retryable()).Return(retryable());
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var result = await decorator.ExecuteAsync(Args(), Context());

        tool.CallCount.Should().Be(3);
        result.Success.Should().BeFalse();
        result.Title.Should().Be("重试耗尽");
        result.Metadata["maxRetries"].Should().Be(3);
        result.Metadata["lastError"].Should().Be("Unknown");
    }

    [Fact]
    public async Task 中途成功_应返回成功结果()
    {
        var tool = new ScriptedTool()
            .Throw(new IOException("disk"))
            .Throw(new HttpRequestException("net"))
            .Return(ToolResult.Succeeded("after-retry"));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var result = await decorator.ExecuteAsync(Args(), Context());

        tool.CallCount.Should().Be(3);
        result.Success.Should().BeTrue();
        result.Output.Should().Be("after-retry");
    }

    [Fact]
    public async Task 非白名单异常_应直接抛出_不重试()
    {
        var tool = new ScriptedTool().Throw(new InvalidOperationException("boom"));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var act = async () => await decorator.ExecuteAsync(Args(), Context());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        tool.CallCount.Should().Be(1, "非白名单异常不得进入重试分支");
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    public async Task 白名单异常_应触发重试(Type exceptionType)
    {
        var tool = new ScriptedTool()
            .Throw((Exception)Activator.CreateInstance(exceptionType)!)
            .Throw((Exception)Activator.CreateInstance(exceptionType)!)
            .Return(ToolResult.Succeeded("recovered"));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var result = await decorator.ExecuteAsync(Args(), Context());

        tool.CallCount.Should().Be(3);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TaskCanceledException_未携带取消令牌_应可重试()
    {
        var tool = new ScriptedTool()
            .Throw(new TaskCanceledException("tce"))
            .Return(ToolResult.Succeeded("recovered"));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var result = await decorator.ExecuteAsync(Args(), Context());

        tool.CallCount.Should().Be(2);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TaskCanceledException_已请求取消_不应重试()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = new ScriptedTool()
            .Throw(new TaskCanceledException("cancelled", null, cts.Token));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var act = async () => await decorator.ExecuteAsync(Args(), Context());

        await act.Should().ThrowAsync<TaskCanceledException>();
        tool.CallCount.Should().Be(1, "已取消的 TaskCanceledException 不属于可重试白名单");
    }

    [Fact]
    public async Task 失败结果标记retryable_应重试()
    {
        var retryable = new ToolResult
        {
            Success = false,
            Error = "temporary",
            Metadata = new Dictionary<string, object> { ["retryable"] = true }
        };
        var tool = new ScriptedTool()
            .Return(retryable)
            .Return(ToolResult.Succeeded("ok"));
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var result = await decorator.ExecuteAsync(Args(), Context());

        tool.CallCount.Should().Be(2);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task 失败结果未标记retryable_应立即返回()
    {
        var failed = ToolResult.Failed("permanent");
        var tool = new ScriptedTool().Return(failed);
        var decorator = CreateDecorator(tool, maxRetries: 3);

        var result = await decorator.ExecuteAsync(Args(), Context());

        tool.CallCount.Should().Be(1);
        result.Success.Should().BeFalse();
        result.Error.Should().Be("permanent");
    }

    [Fact]
    public async Task maxRetries_为1_应只尝试一次且不重试()
    {
        var tool = new ScriptedTool().Throw(new TimeoutException("only"));
        var decorator = CreateDecorator(tool, maxRetries: 1);

        var act = async () => await decorator.ExecuteAsync(Args(), Context());

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("only");
        tool.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task 退避_应随尝试次数线性增长()
    {
        // 线性退避：delay×1 + delay×2 = 150ms（若为固定退避则仅 100ms）
        var baseDelay = TimeSpan.FromMilliseconds(50);
        var tool = new ScriptedTool()
            .Throw(new TimeoutException("1"))
            .Throw(new TimeoutException("2"))
            .Throw(new TimeoutException("3"));
        var decorator = CreateDecorator(tool, maxRetries: 3, delay: baseDelay);

        var sw = Stopwatch.StartNew();
        var act = async () => await decorator.ExecuteAsync(Args(), Context());
        await act.Should().ThrowAsync<TimeoutException>();
        sw.Stop();

        tool.CallCount.Should().Be(3);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(130),
            "线性退避应产生 50ms + 100ms 的等待；固定退避仅 100ms");
    }

    private static RetryToolDecorator CreateDecorator(
        ITool inner,
        int maxRetries,
        TimeSpan? delay = null)
        => new(inner, maxRetries: maxRetries, delay: delay ?? TimeSpan.Zero);
}
