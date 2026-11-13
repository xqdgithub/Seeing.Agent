using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Mcp.Tools;
using Seeing.Agent.Mcp.Tools.Manage;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// Task 4：管理工具权限 kind 与 mcp.execute 不互通；必填 level；connect 三态。
/// </summary>
public class McpManageToolsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ManageGate_Uses_ManageWrite_Kind_Not_Execute()
    {
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", CallId = "c1", PermissionAuthorizer = authorizer };

        await McpManagePermissionGate.AuthorizeAsync(
            McpManagePermissionGate.KindWrite, "demo", "mcp_add_server", Args("{}"), context);

        authorizer.Requests.Should().ContainSingle();
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.manage.write");
        authorizer.Requests[0].Resource.Should().Be("demo");
    }

    [Fact]
    public async Task ManageGate_ExecuteGrant_DoesNot_Satisfy_ManageWrite()
    {
        var authorizer = new FakePermissionAuthorizer
        {
            DecisionFor = kind => kind == "mcp.execute" ? PermissionEffect.Allow : PermissionEffect.Deny,
            Reason = "仅 execute"
        };
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        var resolution = await McpManagePermissionGate.AuthorizeAsync(
            McpManagePermissionGate.KindWrite, "demo", "mcp_add_server", Args("{}"), context);

        resolution!.Decision.Should().Be(PermissionEffect.Deny);
    }

    [Fact]
    public async Task ManageGate_NoAuthorizer_FailClosed()
    {
        var context = new ToolContext { SessionId = "s1" };

        var resolution = await McpManagePermissionGate.AuthorizeAsync(
            McpManagePermissionGate.KindRead, "*", "mcp_list_servers", Args("{}"), context);

        resolution!.Decision.Should().Be(PermissionEffect.Deny);
    }

    [Fact]
    public async Task ListServers_Includes_Capabilities_Summary()
    {
        var caps = new McpServerCapabilitiesInfo(true, true, false, false, true, false, false, false);
        var status = new McpServerStatus(
            "srv", McpConnectionState.Connected,
            new McpServerConfig { Command = "npx" }, McpServerPriority.Normal,
            2, ["a", "b"], DateTimeOffset.UtcNow, null, null, null, 0, null,
            capabilities: caps, resourceCount: 3, promptCount: 1, skillCount: 0);

        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.GetAllStatus()).Returns(new Dictionary<string, McpServerStatus> { ["srv"] = status });

        var tool = new McpListServersTool(NullLogger<McpListServersTool>.Instance, manager.Object);
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        var result = await tool.ExecuteAsync(Args("{}"), context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("srv");
        result.Output.Should().Contain("tools=true");
        result.Output.Should().Contain("resources=true");
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.manage.read");
        authorizer.Requests[0].Resource.Should().Be("*");
    }

    [Fact]
    public async Task AddServer_Requires_Level_And_Passes_Connect_Override()
    {
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.AddServerAsync(
                "demo",
                It.IsAny<McpServerConfig>(),
                ConfigLevel.User,
                true,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(McpOperationResult.Succeeded("demo", McpOperationType.Add, McpConnectionState.Pending));

        var tool = new McpAddServerTool(NullLogger<McpAddServerTool>.Instance, manager.Object);
        var context = new ToolContext
        {
            SessionId = "s1",
            PermissionAuthorizer = new FakePermissionAuthorizer()
        };

        var result = await tool.ExecuteAsync(Args("""
            {
              "name": "demo",
              "level": "User",
              "connect": false,
              "transportType": "stdio",
              "command": "npx"
            }
            """), context);

        result.Success.Should().BeTrue();
        manager.Verify(m => m.AddServerAsync(
            "demo",
            It.Is<McpServerConfig>(c => c.Command == "npx"),
            ConfigLevel.User,
            true,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddServer_Missing_Level_Fails_Without_Calling_Manager()
    {
        var manager = new Mock<IMcpManager>();
        var tool = new McpAddServerTool(NullLogger<McpAddServerTool>.Instance, manager.Object);
        var context = new ToolContext
        {
            SessionId = "s1",
            PermissionAuthorizer = new FakePermissionAuthorizer()
        };

        var result = await tool.ExecuteAsync(Args("""
            { "name": "demo", "transportType": "stdio", "command": "npx" }
            """), context);

        result.Success.Should().BeFalse();
        manager.Verify(m => m.AddServerAsync(
            It.IsAny<string>(),
            It.IsAny<McpServerConfig>(),
            It.IsAny<ConfigLevel?>(),
            It.IsAny<bool>(),
            It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportServers_Uses_Level_As_Permission_Resource()
    {
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.ImportFromJsonAsync(
                It.IsAny<string>(), ConfigLevel.Project, false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var tool = new McpImportServersTool(NullLogger<McpImportServersTool>.Instance, manager.Object);
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        var result = await tool.ExecuteAsync(Args("""
            { "level": "Project", "json": "{\"mcpServers\":{}}" }
            """), context);

        result.Success.Should().BeTrue();
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.manage.write");
        authorizer.Requests[0].Resource.Should().Be("Project");
    }

    [Fact]
    public async Task Lifecycle_Reconnect_Uses_Lifecycle_Kind()
    {
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.ReconnectServerAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(McpOperationResult.Succeeded("srv", McpOperationType.Reconnect, McpConnectionState.Connecting));

        var tool = new McpReconnectServerTool(NullLogger<McpReconnectServerTool>.Instance, manager.Object);
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        var result = await tool.ExecuteAsync(Args("""{ "name": "srv" }"""), context);

        result.Success.Should().BeTrue();
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.manage.lifecycle");
        authorizer.Requests[0].Resource.Should().Be("srv");
    }

    [Fact]
    public void Module_ProvidedTools_Lists_All_Manage_Ids()
    {
        var module = new McpModule();
        var expected = new[]
        {
            "mcp_list_servers", "mcp_get_server", "mcp_add_server", "mcp_update_server",
            "mcp_remove_server", "mcp_enable_server", "mcp_disable_server",
            "mcp_pause_server", "mcp_resume_server", "mcp_reconnect_server", "mcp_import_servers"
        };
        module.ProvidedTools.Should().Contain(expected);
    }

    private sealed class FakePermissionAuthorizer : IPermissionAuthorizer
    {
        public string SessionId => "s1";
        public List<PermissionRequest> Requests { get; } = new();
        public Func<string, PermissionEffect>? DecisionFor { get; set; }
        public PermissionEffect Decision { get; set; } = PermissionEffect.Allow;
        public string? Reason { get; set; }

        public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var decision = DecisionFor?.Invoke(request.PermissionKind) ?? Decision;
            return Task.FromResult(new PermissionResolution
            {
                RequestId = "req-1",
                SessionId = request.SessionId,
                Decision = decision,
                ResolvedBy = PermissionResolvedBy.User,
                Reason = Reason
            });
        }
    }
}
