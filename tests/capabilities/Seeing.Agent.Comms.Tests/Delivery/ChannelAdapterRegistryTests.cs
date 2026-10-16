using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.Delivery;

namespace Seeing.Agent.Comms.Tests.Delivery;

public class ChannelAdapterRegistryTests
{
    [Fact]
    public void ChannelAdapterRegistry_DuplicateRegistration_LastWins_AndKeepsChannelsList()
    {
        var logger = new ListLogger();
        var registry = new ChannelAdapterRegistry(logger);
        var first = new Mock<IChannelAdapter>();
        first.Setup(a => a.ChannelId).Returns("qq");
        var second = new Mock<IChannelAdapter>();
        second.Setup(a => a.ChannelId).Returns("qq");
        var wecom = new Mock<IChannelAdapter>();
        wecom.Setup(a => a.ChannelId).Returns("wecom");

        registry.Register(first.Object);
        registry.Register(second.Object);
        registry.Register(wecom.Object);

        registry.TryGet("qq", out var qq).Should().BeTrue();
        qq.Should().BeSameAs(second.Object);
        registry.RegisteredChannels.Should().BeEquivalentTo(["qq", "wecom"]);
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().Contain("qq");
    }

    private sealed class ListLogger : ILogger<ChannelAdapterRegistry>
    {
        public List<string> Warnings { get; } = [];

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
