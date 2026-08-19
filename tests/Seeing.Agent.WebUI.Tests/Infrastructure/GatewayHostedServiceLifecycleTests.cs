using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Hosting;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

public sealed class GatewayHostedServiceLifecycleTests
{
    [Fact]
    public async Task StartAsync_ShouldAwaitGatewayStart_AndStopAsync_ShouldStopOnce()
    {
        var startCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IGatewayServer>();
        gateway.SetupGet(x => x.IsRunning).Returns(true);
        gateway.Setup(x => x.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(startCompletion.Task);
        gateway.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new GatewayHostedService(
            gateway.Object,
            Options.Create(new GatewayOptions { Enabled = true, AutoStart = true }),
            NullLogger<GatewayHostedService>.Instance);

        var startTask = service.StartAsync(CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        startTask.IsCompleted.Should().BeFalse(
            "Gateway 启动必须纳入 Host 生命周期，不能使用 fire-and-forget 回调");

        startCompletion.SetResult(true);
        await startTask;
        gateway.Verify(x => x.StartAsync(It.IsAny<CancellationToken>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
        gateway.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_ShouldSkipWhenGatewayIsNotRunning_EvenIfHostTokenIsCanceled()
    {
        var gateway = new Mock<IGatewayServer>();
        gateway.SetupGet(x => x.IsRunning).Returns(false);

        var service = new GatewayHostedService(
            gateway.Object,
            Options.Create(new GatewayOptions { Enabled = true, AutoStart = true }),
            NullLogger<GatewayHostedService>.Instance);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        await service.StopAsync(stopped.Token);

        gateway.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

