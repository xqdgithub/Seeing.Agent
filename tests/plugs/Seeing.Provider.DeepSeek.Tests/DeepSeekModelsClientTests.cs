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
    public async Task ListModelsAsync_ParsesOpenAiStylePayload()
    {
        var handler = new StubHandler(_ =>
        {
            var json = """{"data":[{"id":"deepseek-chat"},{"id":"deepseek-reasoner"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var client = new DeepSeekModelsClient(handler, NullLogger<DeepSeekModelsClient>.Instance);

        var models = await client.ListModelsAsync("sk-test", TestContext.Current.CancellationToken);

        models.Should().HaveCount(2);
        models[0].Id.Should().Be("deepseek-chat");
        models[0].Name.Should().Be("DeepSeek Chat");
        models[0].Provider.Should().Be("deepseek");
        models[0].Limit.Context.Should().Be(1_000_000);
        models[0].Limit.Output.Should().Be(384_000);
        models[1].Id.Should().Be("deepseek-reasoner");
        models[1].Limit.Context.Should().Be(1_000_000);
        models[1].Options!.Thinking!.Type.Should().Be("enabled");
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
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
