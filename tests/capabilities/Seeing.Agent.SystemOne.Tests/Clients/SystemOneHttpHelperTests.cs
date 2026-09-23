using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class SystemOneHttpHelperTests
{
    private const string ModelsJson = """{"models":[{"name":"jev-latest"}]}""";

    [Fact]
    public async Task PostAsync_SendsBearerTokenAcceptAndCustomHeaders()
    {
        var handler = new CapturingHandler("""{"model":"jev-latest","answers":{}}""");
        using var http = new HttpClient(handler);
        var config = Config();
        config.Headers = new Dictionary<string, string> { ["X-Tenant"] = "acme" };

        await SystemOneHttpHelper.PostAsync<SystemOneRequest, SystemOneResponse>(
            http,
            config,
            SystemOneDefaults.EvaluatePath,
            new SystemOneRequest { State = "hello", Model = "m" },
            TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer test-key");
        handler.LastRequest.Headers.GetValues("Accept").Should().ContainSingle().Which.Should().Be("application/json");
        handler.LastRequest.Headers.GetValues("X-Tenant").Should().ContainSingle().Which.Should().Be("acme");
        handler.LastRequest.RequestUri.Should().Be(new Uri("http://systemone.test/v1/systemone"));
        handler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("state").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task GetAsync_On429Then200_RetriesAndReturnsBody()
    {
        var handler = new SequenceHandler(Json("rate limited", HttpStatusCode.TooManyRequests), Json(ModelsJson));
        using var http = new HttpClient(handler);

        var result = await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        result.Models.Should().ContainSingle().Which.Name.Should().Be("jev-latest");
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_On529Then200_RetriesAndReturnsBody()
    {
        var handler = new SequenceHandler(Json("overloaded", (HttpStatusCode)529), Json(ModelsJson));
        using var http = new HttpClient(handler);

        var result = await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        result.Models.Should().ContainSingle();
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_On429BeyondMaxRetries_ThrowsSystemOneException()
    {
        var handler = new SequenceHandler(
            Json("rate limited", HttpStatusCode.TooManyRequests),
            Json("rate limited", HttpStatusCode.TooManyRequests),
            Json("rate limited", HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(maxRetries: 2), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<SystemOneException>();
        ex.Which.StatusCode.Should().Be(429);
        ex.Which.ResponseBody.Should().Be("rate limited");
        handler.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAsync_On401_ThrowsImmediatelyWithoutRetry()
    {
        var handler = new SequenceHandler(Json("unauthorized", HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<SystemOneException>();
        ex.Which.StatusCode.Should().Be(401);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_On429_RespectsRetryAfterHeader()
    {
        var retryAfter = TimeSpan.FromMilliseconds(80);
        var tooMany = Json("rate limited", HttpStatusCode.TooManyRequests);
        tooMany.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        var handler = new SequenceHandler(tooMany, Json(ModelsJson));
        using var http = new HttpClient(handler);
        // 基础退避压到 1ms，确保测得延迟来自 Retry-After
        var config = Config(retryBaseDelayMs: 1, retryMaxDelayMs: 1);

        var stopwatch = Stopwatch.StartNew();
        await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, config, SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(60));
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_成功状态但JSON非法_保留真实状态码()
    {
        var handler = new SequenceHandler(Json("not-json"));
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<SystemOneException>();
        ex.Which.StatusCode.Should().Be(200);
        ex.Which.ResponseBody.Should().Be("not-json");
        ex.Which.InnerException.Should().BeOfType<JsonException>();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_WhenCallerCancels_DoesNotRetryAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CancelingHandler(cts);
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(maxRetries: 3), SystemOneDefaults.ModelsPath, cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
        handler.RequestCount.Should().Be(1);
    }

    private static SystemOneProviderConfig Config(
        int maxRetries = 2, int retryBaseDelayMs = 1, int retryMaxDelayMs = 1)
        => new()
        {
            Id = "typesafe",
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            MaxRetries = maxRetries,
            RetryBaseDelayMs = retryBaseDelayMs,
            RetryMaxDelayMs = retryMaxDelayMs
        };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = _responses.Count > 0 ? _responses.Dequeue() : Json("{}");
            return Task.FromResult(response);
        }
    }

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

    private sealed class CancelingHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cts;

        public CancelingHandler(CancellationTokenSource cts) => _cts = cts;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            _cts.Cancel();
            throw new TaskCanceledException("调用方已取消");
        }
    }
}
