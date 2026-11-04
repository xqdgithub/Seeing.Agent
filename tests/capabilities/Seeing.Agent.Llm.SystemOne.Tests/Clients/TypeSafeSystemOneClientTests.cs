using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Llm.SystemOne.Clients;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Llm.SystemOne.Tests.Clients;

/// <summary>
/// <see cref="TypeSafeSystemOneClient"/> 作为 <see cref="IDecisionClient"/> +
/// <see cref="IModelCatalogClient"/> 的行为（spec §7.9 / §10.3 阶段 7）。
/// </summary>
public class TypeSafeSystemOneClientTests
{
    [Fact]
    public async Task Evaluate_ShouldParseNoulChoiceScore()
    {
        var handler = new CapturingHandler("""
        {"model":"jev-latest","answers":{
          "n":{"type":"noul","noul":0.7},
          "c":{"type":"choice","choice":"b"},
          "s":{"type":"score","score":3.5}}}
        """);
        var client = CreateClient(handler);

        var response = await ((IDecisionClient)client).EvaluateAsync(new DecisionRequest
        {
            Model = "jev-latest",
            State = "state",
            Questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
            {
                ["n"] = new DecisionQuestion { Type = DecisionQuestionTypes.Noul, Instructions = "q" },
                ["c"] = new DecisionQuestion { Type = DecisionQuestionTypes.Choice, Instructions = "q" },
                ["s"] = new DecisionQuestion { Type = DecisionQuestionTypes.Score, Instructions = "q" }
            }
        }, call: null, TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://systemone.test/v1/systemone"));
        response.Answers["n"].Noul.Should().BeApproximately(0.7, 1e-9);
        response.Answers["c"].Choice.Should().Be("b");
        response.Answers["s"].Score.Should().BeApproximately(3.5, 1e-9);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRequestModelIsEmpty_FillsSingleConfiguredModel()
    {
        var handler = new CapturingHandler("""{"model":"jev-latest","answers":{}}""");
        var config = Config();
        config.Models = new Dictionary<string, ModelConfig> { ["jev-latest"] = new() { Id = "jev-latest" } };
        var client = CreateClient(handler, config);

        await ((IDecisionClient)client).EvaluateAsync(
            new DecisionRequest { State = "state", Model = string.Empty },
            call: null,
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("jev-latest");
    }

    [Fact]
    public async Task EvaluateAsync_WhenRequestModelProvided_DoesNotOverride()
    {
        var handler = new CapturingHandler("""{"model":"explicit-model","answers":{}}""");
        var config = Config();
        config.Models = new Dictionary<string, ModelConfig> { ["jev-latest"] = new() { Id = "jev-latest" } };
        var client = CreateClient(handler, config);

        await ((IDecisionClient)client).EvaluateAsync(
            new DecisionRequest { State = "state", Model = "explicit-model" },
            call: null,
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("explicit-model");
    }

    [Fact]
    public async Task EvaluateAsync_WhenModelEmptyAndMultipleConfiguredModels_ErrsInsteadOfGuessing()
    {
        var handler = new CapturingHandler("""{"answers":{}}""");
        var config = Config();
        config.Models = new Dictionary<string, ModelConfig>
        {
            ["jev-a"] = new() { Id = "jev-a" },
            ["jev-b"] = new() { Id = "jev-b" }
        };
        var client = CreateClient(handler, config);

        var act = () => ((IDecisionClient)client).EvaluateAsync(
            new DecisionRequest { State = "state" },
            call: null,
            TestContext.Current.CancellationToken);

        // spec §13 原则 1：不做「取首个」的启发式
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("jev-a").And.Contain("jev-b");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task DecisionClient_ListModelsAsync_ReturnsDecisionModels()
    {
        var handler = new CapturingHandler("""{"models":[{"name":"jev-latest"},{"name":"jev-mini"}]}""");
        var client = CreateClient(handler);

        var models = await ((IDecisionClient)client).ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().HaveCount(2);
        models[0].Name.Should().Be("jev-latest");
        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://systemone.test/v1/models"));
    }

    [Fact]
    public async Task CatalogClient_ListModelsAsync_MapsToDecisionTypedModelConfigs()
    {
        var handler = new CapturingHandler("""{"models":[{"name":"jev-latest","description":"Jev"}]}""");
        var client = CreateClient(handler);

        var configs = await ((IModelCatalogClient)client).ListModelsAsync(TestContext.Current.CancellationToken);

        var config = configs.Should().ContainSingle().Which;
        config.Id.Should().Be("jev-latest");
        config.Provider.Should().Be("typesafe");
        config.Types.Should().Equal(ModelType.Decision);
    }

    [Fact]
    public async Task EvaluateAsync_PassesCallContextExtraHeaders()
    {
        var handler = new CapturingHandler("""{"answers":{}}""");
        var client = CreateClient(handler);
        var call = new LlmCallContext { SessionId = "s1" };
        call.ExtraHeaders["X-Trace"] = "trace-1";

        await ((IDecisionClient)client).EvaluateAsync(
            new DecisionRequest { State = "s", Model = "jev" },
            call,
            TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("X-Trace").Should().ContainSingle().Which.Should().Be("trace-1");
    }

    [Fact]
    public async Task TestConnectionAsync_DecisionProbe_SendsSingleNoulQuestionAndChecksProbability()
    {
        var handler = new CapturingHandler("""{"model":"jev","answers":{"probe":{"type":"noul","noul":0.5}}}""");
        var client = CreateClient(handler);

        var ok = await client.TestConnectionAsync(
            "jev-latest", LlmCapabilityKind.Decision, null, TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("questions").GetProperty("probe")
            .GetProperty("type").GetString().Should().Be("noul");
    }

    [Fact]
    public async Task TestConnectionAsync_WhenProbeMissingProbability_ReturnsFalse()
    {
        var handler = new CapturingHandler("""{"model":"jev","answers":{}}""");
        var client = CreateClient(handler);

        var ok = await client.TestConnectionAsync(
            "jev-latest", LlmCapabilityKind.Decision, null, TestContext.Current.CancellationToken);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenRequestFails_ReturnsFalse()
    {
        var handler = new FailingHandler(HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        var ok = await client.TestConnectionAsync(
            "jev-latest", LlmCapabilityKind.Decision, null, TestContext.Current.CancellationToken);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_NonDecisionCapability_ReturnsFalseWithoutRequest()
    {
        var handler = new CapturingHandler("""{"answers":{}}""");
        var client = CreateClient(handler);

        var ok = await client.TestConnectionAsync(
            "jev-latest", LlmCapabilityKind.Chat, null, TestContext.Current.CancellationToken);

        ok.Should().BeFalse();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public void Identity_ComesFromProviderConfig()
    {
        var handler = new CapturingHandler("{}");
        var client = CreateClient(handler);

        client.ProviderId.Should().Be("typesafe");
        client.ProviderType.Should().Be(ProviderTypes.TypeSafe);
    }

    private static TypeSafeSystemOneClient CreateClient(
        HttpMessageHandler handler, ProviderConfig? config = null)
        => new(
            config ?? Config(),
            new HttpClient(handler) { BaseAddress = null },
            NullLogger.Instance);

    private static ProviderConfig Config()
        => new()
        {
            Id = "typesafe",
            Type = ProviderTypes.TypeSafe,
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            Timeout = 10000
        };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CapturingHandler(string responseBody) => _responseBody = responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
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
