using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Embedding;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

public class EmbeddingStatusTests
{
    [Fact]
    public void EmbeddingStatus_WhenNotConfigured_ShouldBeUnavailable()
    {
        var status = new ConfigurableEmbeddingStatus(
            new OptionsMonitorStub(new MemoryOptions()));
        status.IsAvailable.Should().BeFalse();
        status.Reason.Should().Contain("not configured");
    }

    [Fact]
    public void EmbeddingStatus_WhenConfigured_ShouldBeAvailable()
    {
        var status = new ConfigurableEmbeddingStatus(new OptionsMonitorStub(new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions
            {
                Provider = "openai",
                Model = "text-embedding-3-small"
            }
        }));
        status.IsAvailable.Should().BeTrue();
        status.Reason.Should().BeNull();
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<MemoryOptions>
    {
        private readonly MemoryOptions _value;
        public OptionsMonitorStub(MemoryOptions value) => _value = value;
        public MemoryOptions CurrentValue => _value;
        public MemoryOptions Get(string? name) => _value;
        public IDisposable OnChange(Action<MemoryOptions, string?> listener) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }
}
