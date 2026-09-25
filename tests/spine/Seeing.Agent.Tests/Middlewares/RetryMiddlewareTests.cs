using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Core.Middlewares;
using Seeing.Agent.Core.Models;
using Xunit;

namespace Seeing.Agent.Tests.Middlewares;

/// <summary>
/// RetryMiddleware 契约测试 — 重试次数、指数退避、异常白名单与取消守卫。
/// <para>
/// 契约判定：中间件位于泛型执行管道中间，无法构造泛型 <c>TResult</c> 的降级结果，
/// 故“重试耗尽”语义为**上抛 <see cref="MaxRetriesExceededException"/> 并包装最后一次异常**
/// （区别于 RetryToolDecorator 的返回 Failure——后者是具体工具包装，知道结果类型）。
/// </para>
/// <para>
/// 锁定语义：总尝试次数 = maxRetries；可重试异常白名单 =
/// TimeoutException / HttpRequestException / IOException / TaskCanceledException（未取消）；
/// 白名单异常在**最后一次尝试**也被捕获并汇总为 MaxRetriesExceededException；
/// 非白名单异常立即上抛；调用方取消时不重试且不吞取消。
/// </para>
/// </summary>
public class RetryMiddlewareTests
{
    private static RetryMiddleware Create(int maxRetries = 3, TimeSpan? delay = null)
        => new(NullLogger<RetryMiddleware>.Instance, maxRetries, delay ?? TimeSpan.Zero);

    private static DefaultExecutionContext Context(CancellationToken token = default)
        => new(new ServiceCollection().BuildServiceProvider(), NullLogger.Instance)
        {
            CancellationToken = token
        };

    [Fact]
    public async Task 首次成功_不应重试()
    {
        var middleware = Create();
        var calls = 0;

        var result = await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                return Task.FromResult("ok");
            },
            Context());

        result.Should().Be("ok");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task 可重试异常耗尽_应抛出MaxRetriesExceededException并包装最后异常()
    {
        var middleware = Create(maxRetries: 3);
        var last = new TimeoutException("t3");
        var calls = 0;

        var act = async () => await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                throw calls < 3 ? new TimeoutException($"t{calls}") : last;
            },
            Context());

        var thrown = await act.Should().ThrowAsync<MaxRetriesExceededException>(
            "中间件无法构造泛型 TResult，重试耗尽以上抛异常表达");

        thrown.Which.MaxRetries.Should().Be(3);
        thrown.Which.InnerException.Should().BeSameAs(last, "耗尽异常应包装最后一次尝试的异常");
        calls.Should().Be(3, "maxRetries=3 表示总共 3 次尝试");
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    public async Task 白名单异常_应触发重试(Type exceptionType)
    {
        var middleware = Create(maxRetries: 3);
        var calls = 0;

        var result = await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                if (calls < 3)
                    throw (Exception)Activator.CreateInstance(exceptionType)!;
                return Task.FromResult("recovered");
            },
            Context());

        calls.Should().Be(3);
        result.Should().Be("recovered");
    }

    [Fact]
    public async Task 非白名单异常_应直接抛出_不重试()
    {
        var middleware = Create(maxRetries: 3);
        var calls = 0;

        var act = async () => await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            },
            Context());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        calls.Should().Be(1, "非白名单异常不得进入重试分支");
    }

    [Fact]
    public async Task maxRetries为1_应只尝试一次并抛出耗尽异常()
    {
        var middleware = Create(maxRetries: 1);
        var calls = 0;

        var act = async () => await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                throw new TimeoutException("only");
            },
            Context());

        var thrown = await act.Should().ThrowAsync<MaxRetriesExceededException>();
        thrown.Which.InnerException.Should().BeOfType<TimeoutException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task 上下文已取消_可重试异常不应被重试()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var middleware = Create(maxRetries: 3);
        var calls = 0;

        var act = async () => await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                throw new TimeoutException("cancelled-by-caller");
            },
            Context(cts.Token));

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("cancelled-by-caller");
        calls.Should().Be(1, "调用方已取消时不得吞掉异常继续重试");
    }

    [Fact]
    public async Task 退避_应随尝试次数指数增长()
    {
        // 指数退避：50×1 + 50×2 + 50×4 = 350ms（线性仅为 50+100+150 = 300ms）
        var middleware = Create(maxRetries: 4, delay: TimeSpan.FromMilliseconds(50));
        var calls = 0;

        var sw = Stopwatch.StartNew();
        var act = async () => await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                throw new TimeoutException($"{calls}");
            },
            Context());
        await act.Should().ThrowAsync<MaxRetriesExceededException>();
        sw.Stop();

        calls.Should().Be(4);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(330),
            "指数退避应产生 50ms + 100ms + 200ms = 350ms 的等待；线性仅 300ms");
    }

    [Fact]
    public async Task 退避期间取消_应传入令牌并快速中止()
    {
        using var cts = new CancellationTokenSource();
        var middleware = Create(maxRetries: 3, delay: TimeSpan.FromSeconds(5));
        var calls = 0;

        var sw = Stopwatch.StartNew();
        var act = async () => await middleware.InvokeAsync<DefaultExecutionContext, string>(
            _ =>
            {
                calls++;
                cts.CancelAfter(TimeSpan.FromMilliseconds(50));
                throw new TimeoutException("slow");
            },
            Context(cts.Token));
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        calls.Should().Be(1);
        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "Task.Delay 应接收取消令牌，取消后不得空等完整退避时长");
    }
}
