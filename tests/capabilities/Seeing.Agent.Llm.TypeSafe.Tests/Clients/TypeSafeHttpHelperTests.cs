using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.TypeSafe.Clients;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Llm.TypeSafe.Tests.Clients;

/// <summary>
/// 序列化、鉴权头与错误映射用例。
/// <para>429/529 退避重试用例已随客户端自带重试删除而移除（spec §6.3）：
/// 退避由框架装饰器链统一施加（工厂内），此处只锁定「单次请求 + 原始错误上抛」。</para>
/// </summary>
public class TypeSafeHttpHelperTests
{
    private const string ModelsJson = """{"models":[{"name":"jev-latest"}]}""";

    [Fact]
    public async Task PostAsync_SendsBearerTokenAcceptAndCustomHeaders()
    {
        var handler = new CapturingHandler("""{"model":"jev-latest","answers":{}}""");
        using var http = new HttpClient(handler);
        var config = Config();
        config.Headers = new Dictionary<string, string> { ["X-Tenant"] = "acme" };

        await TypeSafeHttpHelper.PostAsync<DecisionRequestWire, DecisionResponseWire>(
            http,
            config,
            TypeSafeProtocol.EvaluatePath,
            new DecisionRequestWire { State = "hello", Model = "m" },
            call: null,
            TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer test-key");
        handler.LastRequest.Headers.GetValues("Accept").Should().ContainSingle().Which.Should().Be("application/json");
        handler.LastRequest.Headers.GetValues("X-Tenant").Should().ContainSingle().Which.Should().Be("acme");
        handler.LastRequest.RequestUri.Should().Be(new Uri("http://typesafe.test/v1/systemone"));
        handler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("state").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task PostAsync_ConfigAuthorization_OverridesDefaultBearer()
    {
        var handler = new CapturingHandler("""{"answers":{}}""");
        using var http = new HttpClient(handler);
        var config = Config();
        config.Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer custom" };

        await TypeSafeHttpHelper.PostAsync<DecisionRequestWire, DecisionResponseWire>(
            http, config, TypeSafeProtocol.EvaluatePath,
            new DecisionRequestWire { State = "s", Model = "m" },
            call: null, TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("Authorization")
            .Should().ContainSingle().Which.Should().Be("Bearer custom");
    }

    [Fact]
    public async Task PostAsync_CallContextHeaders_AreAppliedPerRequest()
    {
        var handler = new CapturingHandler("""{"answers":{}}""");
        using var http = new HttpClient(handler);
        var call = new LlmCallContext();
        call.ExtraHeaders["X-Trace"] = "t-1";

        await TypeSafeHttpHelper.PostAsync<DecisionRequestWire, DecisionResponseWire>(
            http, Config(), TypeSafeProtocol.EvaluatePath,
            new DecisionRequestWire { State = "s", Model = "m" },
            call, TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("X-Trace").Should().ContainSingle().Which.Should().Be("t-1");
    }

    [Fact]
    public async Task GetAsync_On429_ThrowsImmediatelyWithoutRetry()
    {
        var handler = new SequenceHandler(Json("rate limited", HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);

        var act = () => TypeSafeHttpHelper.GetAsync<ModelsResponseWire>(
            http, Config(), TypeSafeProtocol.ModelsPath, call: null, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<DecisionProtocolException>();
        ex.Which.StatusCode.Should().Be(429);
        ex.Which.ResponseBody.Should().Be("rate limited");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_On529_ThrowsImmediatelyWithoutRetry()
    {
        var handler = new SequenceHandler(Json("overloaded", (HttpStatusCode)529));
        using var http = new HttpClient(handler);

        var act = () => TypeSafeHttpHelper.GetAsync<ModelsResponseWire>(
            http, Config(), TypeSafeProtocol.ModelsPath, call: null, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<DecisionProtocolException>();
        ex.Which.StatusCode.Should().Be(529);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_On401_ThrowsImmediatelyWithoutRetry()
    {
        var handler = new SequenceHandler(Json("unauthorized", HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);

        var act = () => TypeSafeHttpHelper.GetAsync<ModelsResponseWire>(
            http, Config(), TypeSafeProtocol.ModelsPath, call: null, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<DecisionProtocolException>();
        ex.Which.StatusCode.Should().Be(401);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_成功状态但JSON非法_保留真实状态码()
    {
        var handler = new SequenceHandler(Json("not-json"));
        using var http = new HttpClient(handler);

        var act = () => TypeSafeHttpHelper.GetAsync<ModelsResponseWire>(
            http, Config(), TypeSafeProtocol.ModelsPath, call: null, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<DecisionProtocolException>();
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

        var act = () => TypeSafeHttpHelper.GetAsync<ModelsResponseWire>(
            http, Config(), TypeSafeProtocol.ModelsPath, call: null, cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_WithoutBaseUrl_UsesProtocolDefaultHost()
    {
        var handler = new SequenceHandler(Json(ModelsJson));
        using var http = new HttpClient(handler);
        var config = Config();
        config.BaseUrl = null;

        await TypeSafeHttpHelper.GetAsync<ModelsResponseWire>(
            http, config, TypeSafeProtocol.ModelsPath, call: null, TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri.Should().Be(new Uri("https://api.typesafe.ai/v1/models"));
    }

    /// <summary>
    /// 调用拦截器必须对 decision 调用生效——这是与 OpenAI 协议工厂对齐的关键点。
    /// <para>早先 <c>ApplyHeaders</c> 硬编码传 <c>Array.Empty&lt;ILlmCallInterceptor&gt;()</c>，
    /// 导致 <c>llm.params</c>/<c>llm.headers</c> 这类横切机制对 decision 调用静默失效。
    /// 本用例把「拦截器真的被调用、且塑形后的头真的发出去了」钉住。</para>
    /// </summary>
    [Fact]
    public async Task PostAsync_ShouldApplyInterceptors()
    {
        var handler = new CapturingHandler("""{"model":"jev-latest","answers":{}}""");
        using var http = new HttpClient(handler);

        var interceptor = new StampInterceptor();

        await TypeSafeHttpHelper.PostAsync<DecisionRequestWire, DecisionResponseWire>(
            http,
            Config(),
            TypeSafeProtocol.EvaluatePath,
            new DecisionRequestWire { State = "hello", Model = "m" },
            call: null,
            TestContext.Current.CancellationToken,
            interceptors: [interceptor]);

        interceptor.Calls.Should().Be(1, "拦截器必须被调用");
        interceptor.SeenProviderId.Should().Be("typesafe");
        handler.LastRequest!.Headers.GetValues("X-Stamp")
            .Should().ContainSingle().Which.Should().Be("intercepted");
    }

    /// <summary>拦截器塑形的头覆盖静态头（同名时后者胜）。</summary>
    [Fact]
    public async Task PostAsync_Interceptor_ShouldOverrideStaticHeader()
    {
        var handler = new CapturingHandler("""{"model":"jev-latest","answers":{}}""");
        using var http = new HttpClient(handler);
        var config = Config();
        config.Headers = new Dictionary<string, string> { ["X-Stamp"] = "static" };

        await TypeSafeHttpHelper.PostAsync<DecisionRequestWire, DecisionResponseWire>(
            http,
            config,
            TypeSafeProtocol.EvaluatePath,
            new DecisionRequestWire { State = "hello", Model = "m" },
            call: null,
            TestContext.Current.CancellationToken,
            interceptors: [new StampInterceptor()]);

        handler.LastRequest!.Headers.GetValues("X-Stamp")
            .Should().ContainSingle().Which.Should().Be("intercepted");
    }

    private sealed class StampInterceptor : ILlmCallInterceptor
    {
        public int Calls { get; private set; }
        public string? SeenProviderId { get; private set; }

        public int Order => 0;

        public bool AppliesTo(string providerId, string providerType) => true;

        public void OnSending(LlmOutboundRequest request)
        {
            Calls++;
            SeenProviderId = request.ProviderId;
            request.Headers["X-Stamp"] = "intercepted";
        }
    }

    private static ProviderConfig Config()
        => new()
        {
            Id = "typesafe",
            Type = ProviderTypes.TypeSafe,
            BaseUrl = "http://typesafe.test",
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

        public HttpRequestMessage? LastRequest { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
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
