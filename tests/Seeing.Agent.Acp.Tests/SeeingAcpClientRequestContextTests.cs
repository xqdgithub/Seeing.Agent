using Acp.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Filesystem;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class SeeingAcpClientRequestContextTests
{
    [Fact]
    public async Task ConfigureForRequest_ShouldRouteSessionUpdatesToLatestSink()
    {
        var client = CreateClient();
        var firstSink = new BufferingSink();
        var secondSink = new BufferingSink();

        client.ConfigureForRequest(firstSink, permissionContext: null);
        await client.SessionUpdateAsync(
            "acp-1",
            new AgentMessageChunk { Content = new TextContentBlock("first") });

        client.ConfigureForRequest(secondSink, permissionContext: null);
        await client.SessionUpdateAsync(
            "acp-1",
            new AgentMessageChunk { Content = new TextContentBlock("second") });

        firstSink.GetText().Should().Contain("first");
        firstSink.GetText().Should().NotContain("second");
        secondSink.GetText().Should().Contain("second");
    }

    private static SeeingAcpClient CreateClient()
    {
        var backend = new AcpBackendDescriptor
        {
            Id = "test",
            Command = "cmd.exe"
        };

        var options = Options.Create(new SeeingAgentOptions());

        return new SeeingAcpClient(
            backend,
            new AcpPermissionBridge(NullLogger<AcpPermissionBridge>.Instance),
            new AcpFileSystemBridge(NullLogger<AcpFileSystemBridge>.Instance),
            new AcpTerminalBridge(NullLogger<AcpTerminalBridge>.Instance),
            options,
            NullLogger<SeeingAcpClient>.Instance);
    }
}
