using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Anthropic.Clients;
using System.Net;
using System.Text;
using Xunit;

namespace Seeing.Agent.Llm.Anthropic.Tests;

/// <summary>
/// Anthropic 经 <see cref="IModelCatalogClient"/> 拉取远端模型目录。
/// </summary>
public class AnthropicModelCatalogClientTests
{
    private static ProviderConfig CreateConfig() => new()
    {
        Id = "anthropic",
        Type = ProviderTypes.Anthropic,
        BaseUrl = "https://api.anthropic.com/v1",
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
    public async Task ListModelsAsync_ShouldGetModelsAndMapConfigs()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "data": [
                    {
                      "id": "claude-sonnet-4-5",
                      "display_name": "Claude Sonnet 4.5",
                      "type": "model",
                      "lifecycle": "active",
                      "max_input_tokens": 200000,
                      "max_tokens": 64000,
                      "capabilities": {
                        "image_input": { "supported": true },
                        "thinking": {
                          "supported": true,
                          "types": {
                            "enabled": { "supported": true },
                            "disabled": { "supported": true }
                          }
                        }
                      }
                    },
                    {
                      "id": "",
                      "display_name": "broken"
                    }
                  ],
                  "has_more": false
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        using var client = new AnthropicClient(CreateConfig(), http, NullLogger.Instance);

        var catalog = client.Should().BeAssignableTo<IModelCatalogClient>().Subject;
        var models = await catalog.ListModelsAsync(TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.ToString().Should().Contain("/models");

        models.Should().ContainSingle();
        models[0].Id.Should().Be("claude-sonnet-4-5");
        models[0].Name.Should().Be("Claude Sonnet 4.5");
        models[0].Provider.Should().Be("anthropic");
        models[0].Limit.Context.Should().Be(200000);
        models[0].Limit.Output.Should().Be(64000);
        models[0].Modalities.Input.Should().Contain("image");
        models[0].Options!.Thinking!.Supported.Should().BeTrue();
    }

    [Fact]
    public async Task ListModelsAsync_ShouldPaginateUntilDone()
    {
        var handler = new StubHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("after_id=", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"claude-haiku-4","display_name":"Haiku"}],"has_more":false}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"claude-opus-4","display_name":"Opus"}],"has_more":true,"last_id":"claude-opus-4"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        using var client = new AnthropicClient(CreateConfig(), http, NullLogger.Instance);

        var models = await ((IModelCatalogClient)client)
            .ListModelsAsync(TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].RequestUri!.ToString().Should().Contain("after_id=claude-opus-4");
        models.Select(m => m.Id).Should().Equal("claude-opus-4", "claude-haiku-4");
    }

    [Fact]
    public async Task ListModelsAsync_NonSuccess_ShouldReturnEmpty()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"bad key\"}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        using var client = new AnthropicClient(CreateConfig(), http, NullLogger.Instance);

        var models = await ((IModelCatalogClient)client)
            .ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
    }
}
