using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Llm.SystemOne.Clients;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Llm.SystemOne.Tests.Clients;

/// <summary>
/// 序列化、鉴权头与错误映射用例。
/// <para>429/529 退避重试用例已随客户端自带重试删除而移除（spec §6.3）：
/// 退避由框架装饰器链统一负责，此处只锁定「单次请求 + 原始错误上抛」。</para>
/// </summary>
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
    public async Task GetAsync_On429_ThrowsImmediatelyWithoutRetry()
    {
        var handler = new SequenceHandler(Json("rate limited", HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<SystemOneException>();
        ex.Which.StatusCode.Should().Be(429);
        ex.Which.ResponseBody.Should().Be("rate limited");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_On529_ThrowsImmediatelyWithoutRetry()
    {
        var handler = new SequenceHandler(Json("overloaded", (HttpStatusCode)529));
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<SystemOneException>();
        ex.Which.StatusCode.Should().Be(529);
        handler.RequestCount.Should().Be(1);
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
    public async Task GetAsync_WhenCallerCancels_DoesNotWrapAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CancelingHandler(cts);
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
        handler.RequestCount.Should().Be(1);
    }

    private static SystemOneProviderConfig Config()
        => new()
        {
            Id = "typesafe",
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key"
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
