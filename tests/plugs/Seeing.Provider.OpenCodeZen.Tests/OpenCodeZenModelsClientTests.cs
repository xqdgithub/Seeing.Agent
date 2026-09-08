using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.OpenCodeZen;
using Xunit;

namespace Seeing.Provider.OpenCodeZen.Tests;

public class OpenCodeZenModelsClientTests
{
    [Fact]
    public async Task ListModelsAsync_ParsesPayload_AndMarksFreeModels()
    {
        var json = """{"data":[{"id":"nemotron-3-ultra-free"},{"id":"deepseek-v4-pro"},{"id":"big-pickle"}]}""";
        var client = CreateClient(json);

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().HaveCount(3);
        var freeModel = models.Single(m => m.Id == "nemotron-3-ultra-free");
        freeModel.IsFree.Should().BeTrue();
        freeModel.InputPrice.Should().Be(0);
        freeModel.OutputPrice.Should().Be(0);
        freeModel.Context.Should().Be(200_000);

        var bigPickle = models.Single(m => m.Id == "big-pickle");
        bigPickle.IsFree.Should().BeTrue();

        var paid = models.Single(m => m.Id == "deepseek-v4-pro");
        paid.IsFree.Should().BeFalse();
        paid.InputPrice.Should().BeNull();
        paid.OutputPrice.Should().BeNull();
    }

    [Fact]
    public async Task ListModelsAsync_HttpError_ReturnsEmpty()
    {
        var client = new OpenCodeZenModelsClient(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)),
            NullLogger<OpenCodeZenModelsClient>.Instance);

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task ListModelsAsync_MalformedJson_ReturnsEmpty()
    {
        var client = CreateClient("{ not-json");

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task ListModelsAsync_EmptyData_ReturnsEmpty()
    {
        var client = CreateClient("""{"data":[]}""");

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
    }

    private static OpenCodeZenModelsClient CreateClient(string json)
        => new(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }),
            NullLogger<OpenCodeZenModelsClient>.Instance);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
