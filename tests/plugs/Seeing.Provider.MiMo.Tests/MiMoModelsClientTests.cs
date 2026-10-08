using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.MiMo;
using Xunit;

namespace Seeing.Provider.MiMo.Tests;

public class MiMoModelsClientTests
{
    [Fact]
    public async Task ListModelsAsync_ParsesPayload_AndKeepsAllEntries()
    {
        var handler = new StubHandler(_ =>
        {
            var json = """
                {"object":"list","data":[
                    {"id":"mimo-v2.6-pro","object":"model","owned_by":"xiaomi"},
                    {"id":"mimo-v2.5-asr","object":"model","owned_by":"xiaomi"},
                    {"id":"mimo-v2.5-tts","object":"model","owned_by":"xiaomi"}
                ]}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var client = new MiMoModelsClient(handler, NullLogger<MiMoModelsClient>.Instance);

        var models = await client.ListModelsAsync("sk-test", TestContext.Current.CancellationToken);

        // 需求：非对话模型（asr/tts）原样保留，不过滤
        models.Select(m => m.Id).Should().Equal("mimo-v2.6-pro", "mimo-v2.5-asr", "mimo-v2.5-tts");
        models.Should().OnlyContain(m => m.Provider == "mimo");
        models[0].Name.Should().Be("mimo-v2.6-pro");
        models[0].Options.Should().BeNull();
    }

    [Fact]
    public async Task ListModelsAsync_UsesBearerAuthorization()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"object":"list","data":[]}""", Encoding.UTF8, "application/json")
            };
        });
        var client = new MiMoModelsClient(handler, NullLogger<MiMoModelsClient>.Instance);

        await client.ListModelsAsync("sk-secret", TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("sk-secret");
        captured.RequestUri!.ToString().Should().EndWith("/v1/models");
    }

    [Fact]
    public async Task ListModelsAsync_WithoutApiKey_ReturnsEmptyWithoutRequest()
    {
        var requested = false;
        var handler = new StubHandler(_ =>
        {
            requested = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new MiMoModelsClient(handler, NullLogger<MiMoModelsClient>.Instance);

        var models = await client.ListModelsAsync(" ", TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
        requested.Should().BeFalse();
    }

    [Fact]
    public async Task ListModelsAsync_HttpError_ReturnsEmpty()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new MiMoModelsClient(handler, NullLogger<MiMoModelsClient>.Instance);

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
