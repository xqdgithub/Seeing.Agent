using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// OpenAiEmbeddingClient 请求体 / index 乱序回排 / usage 映射 / 真实探针连接测试（spec §7.8 + 决议 25）。
/// </summary>
public class OpenAiEmbeddingClientTests
{
    private static ProviderConfig CreateConfig() => new()
    {
        Id = "test-provider",
        Type = ProviderTypes.OpenAi,
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "test-key"
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public readonly List<(HttpRequestMessage Request, string Body)> Requests = new();

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return _respond();
        }
    }

    private static (OpenAiEmbeddingClient Client, StubHandler Handler) CreateSut(
        Func<HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (new OpenAiEmbeddingClient(CreateConfig(), httpClient, NullLogger.Instance), handler);
    }

    [Fact]
    public async Task EmbedAsync_ShouldPostToEmbeddingsEndpoint_WithModelAndInput()
    {
        var (client, handler) = CreateSut(() => JsonResponse(
            "{\"data\":[{\"index\":0,\"embedding\":[0.1,0.2]}]}"));

        var response = await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Inputs = new[] { "第一句", "第二句" }
        });

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Request.RequestUri!.AbsolutePath.Should().Be("/embeddings");

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("text-embedding-3-small");
        var input = doc.RootElement.GetProperty("input");
        input.ValueKind.Should().Be(JsonValueKind.Array);
        input.GetArrayLength().Should().Be(2);
        input[0].GetString().Should().Be("第一句");
        input[1].GetString().Should().Be("第二句");

        response.Vectors.Should().HaveCount(1);
    }

    [Fact]
    public async Task EmbedAsync_OutOfOrderIndexes_ShouldSortVectorsByIndexAscending()
    {
        // Arrange：服务端并行批处理导致 data[] 乱序返回（index 1 在前、0 在后）
        var (client, _) = CreateSut(() => JsonResponse(
            """
            {"data":[
              {"index":1,"embedding":[1.5,1.6]},
              {"index":0,"embedding":[0.5,0.6]}
            ]}
            """));

        var response = await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Inputs = new[] { "甲", "乙" }
        });

        // Assert：按 Index 升序回排，Text 与原始输入顺序对齐
        response.Vectors.Select(v => v.Index).Should().Equal(0, 1);
        response.Vectors[0].Text.Should().Be("甲");
        response.Vectors[1].Text.Should().Be("乙");
        response.Vectors[0].Values.Should().Equal(0.5f, 0.6f);
        response.Vectors[1].Values.Should().Equal(1.5f, 1.6f);
    }

    [Fact]
    public async Task EmbedAsync_ShouldReportDimensionsAndMapUsage()
    {
        var (client, _) = CreateSut(() => JsonResponse(
            """
            {"data":[{"index":0,"embedding":[0.1,0.2,0.3]},{"index":1,"embedding":[0.4,0.5,0.6]}],
             "usage":{"prompt_tokens":8,"total_tokens":8}}
            """));

        var response = await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Inputs = new[] { "a", "b" }
        });

        response.Dimensions.Should().Be(3);
        response.Usage.Should().NotBeNull();
        response.Usage!.InputTokens.Should().Be(8);
        response.Usage.OutputTokens.Should().Be(0);
    }

    [Fact]
    public async Task EmbedAsync_DimensionsAndEncodingFormatSet_ShouldPassThroughInBody()
    {
        var (client, handler) = CreateSut(() => JsonResponse(
            "{\"data\":[{\"index\":0,\"embedding\":[0.1]}]}"));

        await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "text-embedding-3-large",
            Inputs = new[] { "x" },
            Dimensions = 64,
            EncodingFormat = "float"
        });

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        doc.RootElement.GetProperty("dimensions").GetInt32().Should().Be(64);
        doc.RootElement.GetProperty("encoding_format").GetString().Should().Be("float");
    }

    [Fact]
    public async Task EmbedAsync_DimensionsAndEncodingFormatUnset_ShouldOmitFromBody()
    {
        var (client, handler) = CreateSut(() => JsonResponse(
            "{\"data\":[{\"index\":0,\"embedding\":[0.1]}]}"));

        await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "text-embedding-ada-002",
            Inputs = new[] { "x" }
        });

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        doc.RootElement.TryGetProperty("dimensions", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("encoding_format", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EmbedAsync_EmptyInputs_ShouldNotSendRequestAndReturnEmptyVectors()
    {
        var (client, handler) = CreateSut(() => JsonResponse("{}"));

        var response = await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Inputs = Array.Empty<string>()
        });

        handler.Requests.Should().BeEmpty("空输入不应发起 HTTP 请求");
        response.Vectors.Should().BeEmpty();
        response.Dimensions.Should().Be(0);
    }

    [Fact]
    public async Task TestConnectionAsync_EmbeddingProbe_ShouldReturnTrueOnlyWhenDimensionsPositive()
    {
        // 决议 25：单条短文本真实探针，校验维度 > 0
        var (okClient, okHandler) = CreateSut(() => JsonResponse(
            "{\"data\":[{\"index\":0,\"embedding\":[0.1,0.2]}]}"));
        (await okClient.TestConnectionAsync("text-embedding-3-small", LlmCapabilityKind.Embedding))
            .Should().BeTrue();
        using var okDoc = JsonDocument.Parse(okHandler.Requests[0].Body);
        okDoc.RootElement.GetProperty("input").GetArrayLength().Should().Be(1);

        // 空向量（维度 0）→ 判定失败
        var (zeroClient, _) = CreateSut(() => JsonResponse(
            "{\"data\":[{\"index\":0,\"embedding\":[]}]}"));
        (await zeroClient.TestConnectionAsync("text-embedding-3-small", LlmCapabilityKind.Embedding))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_HttpFailure_ShouldReturnFalseNotThrow()
    {
        var (client, _) = CreateSut(() => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":{\"message\":\"model not found\"}}")
        });

        (await client.TestConnectionAsync("no-such-model", LlmCapabilityKind.Embedding))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_NonEmbeddingCapability_ShouldReturnFalse()
    {
        var (client, handler) = CreateSut(() => JsonResponse("{}"));

        (await client.TestConnectionAsync("gpt-test", LlmCapabilityKind.Chat)).Should().BeFalse();
        handler.Requests.Should().BeEmpty("能力不匹配时不应发探针请求");
    }

    [Fact]
    public void Identity_ShouldComeFromConfig()
    {
        var (client, _) = CreateSut(() => new HttpResponseMessage(HttpStatusCode.OK));

        client.ProviderId.Should().Be("test-provider");
        client.ProviderType.Should().Be(ProviderTypes.OpenAi);
    }
}
