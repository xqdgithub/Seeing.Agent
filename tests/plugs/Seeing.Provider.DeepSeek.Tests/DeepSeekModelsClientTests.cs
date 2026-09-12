using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekModelsClientTests
{
    [Fact]
    public async Task ListModelsAsync_ParsesOpenAiStylePayload_WithoutStaticPresets()
    {
        var handler = new StubHandler(_ =>
        {
            var json = """{"data":[{"id":"deepseek-v4-flash"},{"id":"deepseek-v4-pro"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var client = new DeepSeekModelsClient(handler, NullLogger<DeepSeekModelsClient>.Instance);

        var models = await client.ListModelsAsync("sk-test", TestContext.Current.CancellationToken);

        models.Should().HaveCount(2);
        models[0].Id.Should().Be("deepseek-v4-flash");
        models[0].Name.Should().Be("deepseek-v4-flash");
        models[0].Provider.Should().Be("deepseek");
        models[0].Limit.Context.Should().Be(4096);
        models[0].Options.Should().BeNull();
        models[1].Id.Should().Be("deepseek-v4-pro");
        models[1].Options.Should().BeNull();
    }

    [Fact]
    public async Task ListModelsAsync_HttpError_ReturnsEmpty()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new DeepSeekModelsClient(handler, NullLogger<DeepSeekModelsClient>.Instance);

        var models = await client.ListModelsAsync("bad", TestContext.Current.CancellationToken);
        models.Should().BeEmpty();
    }

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
