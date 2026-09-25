using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Web.Tests;

public class CodeSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldSendJsonRpcParamsFieldNotParameters()
    {
        // Arrange
        string? requestBody = null;
        var handler = new CapturingHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseResult("示例代码"), Encoding.UTF8, "text/event-stream")
            };
        });
        var tool = CreateTool(handler);

        // Act
        var result = await tool.ExecuteAsync(Args("React useState"), new ToolContext());

        // Assert
        result.Success.Should().BeTrue();
        requestBody.Should().NotBeNull();

        using var doc = JsonDocument.Parse(requestBody!);
        var root = doc.RootElement;

        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("method").GetString().Should().Be("tools/call");

        // JSON-RPC 2.0 规范字段名为 params
        root.GetProperty("params").GetProperty("name").GetString().Should().Be("get_code_context_exa");
        root.GetProperty("params").GetProperty("arguments").GetProperty("query").GetString()
            .Should().Be("React useState");

        // 不得再出现错误字段名 parameters
        root.TryGetProperty("parameters", out _).Should().BeFalse();
    }

    private static CodeSearchTool CreateTool(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new CodeSearchTool(NullLogger<CodeSearchTool>.Instance, client);
    }

    private static JsonElement Args(string query) => JsonSerializer.SerializeToElement(new { query });

    private static string SseResult(string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            result = new
            {
                content = new[] { new { type = "text", text } }
            }
        });
        return $"data: {payload}\n\n";
    }

    private sealed class CapturingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }
}
