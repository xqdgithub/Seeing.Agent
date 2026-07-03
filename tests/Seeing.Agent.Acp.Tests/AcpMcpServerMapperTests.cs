using Acp.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.MCP;
using SeeingMcpConfig = Seeing.Agent.MCP.McpServerConfig;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpMcpServerMappingTests
{
    [Fact]
    public void TryMap_ShouldSkipDisabledAndStreamableHttp()
    {
        var logger = NullLogger.Instance;

        AcpMcpServerMapping.TryMap("disabled-http", new SeeingMcpConfig
        {
            Disabled = true,
            TransportType = McpTransportType.StreamableHttp,
            Url = new Uri("https://example.com/mcp")
        }, logger).Should().BeNull();

        AcpMcpServerMapping.TryMap("active-http", new SeeingMcpConfig
        {
            TransportType = McpTransportType.StreamableHttp,
            Url = new Uri("https://example.com/mcp")
        }, logger).Should().BeNull();

        var stdio = AcpMcpServerMapping.TryMap("stdio", new SeeingMcpConfig
        {
            TransportType = McpTransportType.Stdio,
            Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Args = ["-c", "echo hi"]
        }, logger);

        stdio.Should().BeOfType<StdioMcpServer>();
        var mapped = (StdioMcpServer)stdio!;
        mapped.Args.Should().NotBeNull();
        mapped.Env.Should().NotBeNull();
    }

    [Fact]
    public void TryMap_Sse_ShouldMapHeadersAsArray()
    {
        var sse = AcpMcpServerMapping.TryMap("sse-server", new SeeingMcpConfig
        {
            TransportType = McpTransportType.Sse,
            Url = new Uri("https://example.com/sse"),
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" }
        }, NullLogger.Instance);

        sse.Should().BeOfType<SseMcpServer>();
        var mapped = (SseMcpServer)sse!;
        mapped.Headers.Should().ContainSingle(h => h.Name == "Authorization" && h.Value == "Bearer token");
    }
}
