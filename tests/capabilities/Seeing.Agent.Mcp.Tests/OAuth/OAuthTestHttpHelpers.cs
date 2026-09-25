using System.Net;
using System.Text;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>记录请求并可返回预设响应的 HttpMessageHandler，用于替代真实 HTTP。</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>最近一次请求的 URL。</summary>
    public Uri? LastRequestUri { get; private set; }

    /// <summary>最近一次请求的表单/正文内容。</summary>
    public string? LastBody { get; private set; }

    /// <summary>请求次数。</summary>
    public int CallCount { get; private set; }

    public static RecordingHandler Json(HttpStatusCode status, string json) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return _responder(request);
    }
}

/// <summary>返回固定 handler 的 IHttpClientFactory 测试替身。</summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
