using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ProviderRegistryTests
{
    [Fact]
    public void Register_NewProvider_AddsToRegistry()
    {
        var sut = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = CreateProvider("test");

        sut.Register(new ProviderRegistration(provider.Object, ProviderOrigin.Configured));

        sut.GetProvider("test").Should().BeSameAs(provider.Object);
        sut.GetProviders()["test"].Should().BeSameAs(provider.Object);
    }

    [Fact]
    public void Register_DuplicateId_OverridesAndLogsWarning()
    {
        var logger = new RecordingLogger<ProviderRegistry>();
        var sut = new ProviderRegistry(logger);
        var original = CreateProvider("test");
        var replacement = CreateProvider("test");

        sut.Register(new ProviderRegistration(original.Object, ProviderOrigin.Configured));
        sut.Register(new ProviderRegistration(replacement.Object, ProviderOrigin.Configured));

        sut.GetProvider("test").Should().BeSameAs(replacement.Object);
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("test", StringComparison.Ordinal));
    }

    [Fact]
    public void Register_DuplicateId_DisposesReplacedAsyncDisposableProvider()
    {
        var sut = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var original = CreateProvider("test");
        var disposable = original.As<IAsyncDisposable>();
        disposable.Setup(candidate => candidate.DisposeAsync()).Returns(ValueTask.CompletedTask);

        sut.Register(new ProviderRegistration(original.Object, ProviderOrigin.Configured));
        sut.Register(new ProviderRegistration(CreateProvider("test").Object, ProviderOrigin.Configured));

        disposable.Verify(candidate => candidate.DisposeAsync(), Times.Once);
    }

    [Fact]
    public void Unregister_ExistingProvider_RemovesAndDisposes()
    {
        var sut = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = CreateProvider("test");
        var disposable = provider.As<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);
        sut.Register(new ProviderRegistration(provider.Object, ProviderOrigin.Configured));

        var removed = sut.Unregister("test");

        removed.Should().BeTrue();
        sut.GetProvider("test").Should().BeNull();
        disposable.Verify(d => d.DisposeAsync(), Times.Once);
    }

    [Fact]
    public void UnregisterByOwner_ExtensionProviders_RemovesAllOwned()
    {
        var sut = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var first = CreateProvider("first");
        var second = CreateProvider("second");
        var other = CreateProvider("other");
        sut.Register(new ProviderRegistration(first.Object, ProviderOrigin.Extension, OwnerId: "extension-a"));
        sut.Register(new ProviderRegistration(second.Object, ProviderOrigin.Extension, OwnerId: "extension-a"));
        sut.Register(new ProviderRegistration(other.Object, ProviderOrigin.Extension, OwnerId: "extension-b"));

        var removed = sut.UnregisterByOwner("extension-a");

        removed.Should().Be(2);
        sut.GetProvider("first").Should().BeNull();
        sut.GetProvider("second").Should().BeNull();
        sut.GetProvider("other").Should().BeSameAs(other.Object);
    }

    [Fact]
    public void ProvidersChanged_OnRegister_RaisesEvent()
    {
        var sut = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var raised = 0;
        sut.ProvidersChanged += (_, _) => raised++;

        sut.Register(new ProviderRegistration(CreateProvider("test").Object, ProviderOrigin.Configured));

        raised.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentReads_DuringRegister_AreConsistent()
    {
        var sut = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        using var start = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;

        var writer = Task.Run(() =>
        {
            start.Wait(cancellationToken);
            for (var i = 0; i < 500; i++)
                sut.Register(new ProviderRegistration(
                    CreateProvider($"provider-{i}").Object, ProviderOrigin.Configured));
        }, cancellationToken);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait(cancellationToken);
            for (var i = 0; i < 500; i++)
            {
                var snapshot = sut.GetProviders();
                foreach (var pair in snapshot)
                {
                    pair.Value.Should().NotBeNull();
                    pair.Value.Id.Should().Be(pair.Key);
                }
            }
        }, cancellationToken)).ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer));

        sut.GetProviders().Should().HaveCount(500);
    }

    private static Mock<ILlmProvider> CreateProvider(string id)
    {
        var provider = new Mock<ILlmProvider>();
        provider.SetupGet(p => p.Id).Returns(id);
        return provider;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
