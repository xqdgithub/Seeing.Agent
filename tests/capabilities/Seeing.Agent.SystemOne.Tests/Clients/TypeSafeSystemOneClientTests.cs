using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class TypeSafeSystemOneClientTests
{
    [Fact]
    public async Task EvaluateAsync_WhenRequestModelIsEmpty_FillsConfigModel()
    {
        var handler = new CapturingHandler("""{"model":"config-model","answers":{}}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        await client.EvaluateAsync(
            new SystemOneRequest { State = "state", Model = string.Empty },
            TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://systemone.test/v1/systemone"));
        handler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("config-model");
    }

    [Fact]
    public async Task EvaluateAsync_WhenRequestModelProvided_DoesNotOverride()
    {
        var handler = new CapturingHandler("""{"model":"explicit-model","answers":{}}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        await client.EvaluateAsync(
            new SystemOneRequest { State = "state", Model = "explicit-model" },
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("explicit-model");
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsParsedModels()
    {
        var handler = new CapturingHandler("""{"models":[{"name":"jev-latest"},{"name":"jev-mini"}]}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().HaveCount(2);
        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://systemone.test/v1/models"));
    }

    [Fact]
    public async Task TestConnectionAsync_WhenRequestFails_ReturnsFalse()
    {
        var handler = new FailingHandler(HttpStatusCode.Unauthorized);
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        var result = await client.TestConnectionAsync(TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenRequestSucceeds_ReturnsTrue()
    {
        var handler = new CapturingHandler("""{"models":[]}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        var result = await client.TestConnectionAsync(TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    private static SystemOneProviderConfig Config(string model)
        => new()
        {
            Id = "typesafe",
            Type = SystemOneProviderTypes.TypeSafe,
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            Model = model,
            MaxRetries = 0,
            RetryBaseDelayMs = 1,
            RetryMaxDelayMs = 1
        };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CapturingHandler(string responseBody) => _responseBody = responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public FailingHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("unauthorized", Encoding.UTF8, "application/json")
            });
    }
}
