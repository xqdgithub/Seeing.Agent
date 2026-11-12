using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using System.Net;
using System.Text;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// OpenAI 兼容端点经 <see cref="IModelCatalogClient"/> 拉取远端模型目录。
/// </summary>
public class OpenAiModelCatalogClientTests
{
    private static ProviderConfig CreateConfig(string id = "local", string type = ProviderTypes.OpenAi) => new()
    {
        Id = id,
        Type = type,
        BaseUrl = "https://api.example.com/v1",
        ApiKey = "test-key"
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<HttpRequestMessage> Requests = [];

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    [Fact]
    public async Task OpenAiChatClient_ListModelsAsync_ShouldGetModelsAndMapConfigs()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"object":"list","data":[{"id":"gpt-test"},{"id":"text-embedding-3-small"},{"id":""}]}""",
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/v1/") };
        using var client = new OpenAiChatClient(CreateConfig(), http, NullLogger.Instance);

        var catalog = client.Should().BeAssignableTo<IModelCatalogClient>().Subject;
        var models = await catalog.ListModelsAsync(TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.ToString().Should().EndWith("/models");

        models.Should().HaveCount(2);
        models[0].Id.Should().Be("gpt-test");
        models[0].Name.Should().Be("gpt-test");
        models[0].Provider.Should().Be("local");
        models[0].Types.Should().BeNull();

        models[1].Id.Should().Be("text-embedding-3-small");
        models[1].Types.Should().ContainSingle().Which.Should().Be(ModelType.Embedding);
        models[1].Modalities.Should().NotBeNull();
        models[1].Modalities!.Input.Should().Contain("text");
        models[1].Modalities.Output.Should().Contain("embedding");
    }

    [Fact]
    public async Task OpenAiResponsesClient_ListModelsAsync_ShouldGetModels()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"o4-mini"}]}""",
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/v1/") };
        using var client = new OpenAiResponsesClient(
            CreateConfig(type: ProviderTypes.OpenAiResponses),
            http,
            NullLogger.Instance);

        var catalog = client.Should().BeAssignableTo<IModelCatalogClient>().Subject;
        var models = await catalog.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().ContainSingle().Which.Id.Should().Be("o4-mini");
        handler.Requests[0].RequestUri!.ToString().Should().EndWith("/models");
    }

    [Fact]
    public async Task ListModelsAsync_NonSuccess_ShouldReturnEmpty()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"bad key\"}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/v1/") };
        using var client = new OpenAiChatClient(CreateConfig(), http, NullLogger.Instance);

        var models = await ((IModelCatalogClient)client)
            .ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAiModelsClient_ListModelsAsync_ShouldMapProviderId()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"llama3"}]}""",
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/v1/") };
        var sut = new OpenAiModelsClient(CreateConfig("ollama"), http, NullLogger.Instance);

        var models = await sut.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().ContainSingle();
        models[0].Id.Should().Be("llama3");
        models[0].Provider.Should().Be("ollama");
    }
}
