using System.Net;
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class ManagementApiClientTests
{
    [Fact]
    public async Task ReachableAsync_WhenServerResponds_ShouldReturnTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        using var client = new ManagementApiClient("http://127.0.0.1:5000", handler);

        var result = await client.ReachableAsync();

        result.Should().BeTrue();
        handler.LastPath.Should().Be("/");
    }

    [Fact]
    public async Task ReachableAsync_WhenNoResponse_ShouldReturnFalse()
    {
        var handler = new StubHandler(null, "");
        using var client = new ManagementApiClient("http://127.0.0.1:5000", handler);

        (await client.ReachableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenNotFound_ShouldReturnFalse()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "");
        using var client = new ManagementApiClient("http://127.0.0.1:8765", handler);

        (await client.HealthCheckAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenSuccess_ShouldReturnTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        using var client = new ManagementApiClient("http://127.0.0.1:8765", handler);

        (await client.HealthCheckAsync()).Should().BeTrue();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;
        private readonly string _body;
        public string? LastPath { get; private set; }

        public StubHandler(HttpStatusCode? status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            if (_status == null)
                throw new HttpRequestException("连接失败");
            var response = new HttpResponseMessage(_status.Value)
            {
                Content = new StringContent(_body)
            };
            return Task.FromResult(response);
        }
    }
}