using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Embedding;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

public class EmbeddingConnectionTesterTests
{
    [Fact]
    public async Task TestAsync_WhenProviderOrModelEmpty_ShouldFail()
    {
        var tester = CreateTester(CreateLookup(), CreateHttpFactory(null));
        var result = await tester.TestAsync(null, null);
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("填写");
    }

    [Fact]
    public async Task TestAsync_WhenProviderMissing_ShouldFail()
    {
        var tester = CreateTester(CreateLookup(), CreateHttpFactory(null));
        var result = await tester.TestAsync("openai", "text-embedding-3-small");
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("未找到");
    }

    [Fact]
    public async Task TestAsync_WhenHttpOk_ShouldSucceedWithDimensions()
    {
        var factory = CreateHttpFactory(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"embedding":[0.1,0.2,0.3],"index":0}],"usage":{"total_tokens":3}}""",
                Encoding.UTF8,
                "application/json")
        });

        var lookup = new Mock<IProviderEndpointLookup>();
        lookup.Setup(l => l.TryGet("openai", out It.Ref<ProviderEndpoint?>.IsAny))
            .Returns((string _, out ProviderEndpoint? ep) =>
            {
                ep = new ProviderEndpoint { BaseUrl = "https://example.com/v1", ApiKey = "k" };
                return true;
            });

        var tester = CreateTester(lookup.Object, factory);
        var result = await tester.TestAsync("openai", "text-embedding-3-small");
        result.Success.Should().BeTrue();
        result.Dimensions.Should().Be(3);
    }

    private static EmbeddingConnectionTester CreateTester(
        IProviderEndpointLookup lookup,
        IHttpClientFactory http)
    {
        return new EmbeddingConnectionTester(
            http,
            new OptionsMonitorStub(new MemoryOptions()),
            lookup);
    }

    private static IProviderEndpointLookup CreateLookup()
    {
        var lookup = new Mock<IProviderEndpointLookup>();
        lookup.Setup(l => l.TryGet(It.IsAny<string>(), out It.Ref<ProviderEndpoint?>.IsAny))
            .Returns(false);
        return lookup.Object;
    }

    private static IHttpClientFactory CreateHttpFactory(HttpResponseMessage? response)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response ?? new HttpResponseMessage(HttpStatusCode.BadRequest));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler.Object));
        return factory.Object;
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
