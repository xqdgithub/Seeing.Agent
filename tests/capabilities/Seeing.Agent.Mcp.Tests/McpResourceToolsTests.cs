using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Mcp.Tools;
using Seeing.Agent.Mcp.Tools.Resources;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// Task 5：Resource 工具 — capabilities 门控、mcp.resource.read 记忆键 server|uri、与 mcp.execute 分离。
/// </summary>
public class McpResourceToolsTests
{
    private static JsonElement Args(object anon) => JsonSerializer.SerializeToElement(anon);

    [Fact]
    public async Task ResourceGate_Read_Uses_Kind_And_ServerPipeUri()
    {
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", CallId = "c1", PermissionAuthorizer = authorizer };

        var resolution = await McpResourcePermissionGate.AuthorizeAsync(
            "demo", "file://a.txt", Args(new { server = "demo", uri = "file://a.txt" }), context);

        resolution.Should().NotBeNull();
        authorizer.Requests.Should().ContainSingle();
        var req = authorizer.Requests[0];
        req.PermissionKind.Should().Be("mcp.resource.read");
        req.Resource.Should().Be("demo|file://a.txt");
        req.PermissionKind.Should().NotBe("mcp.execute");
    }

    [Fact]
    public async Task ResourceGate_List_Uses_ServerPipeStar()
    {
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        await McpResourcePermissionGate.AuthorizeAsync(
            "demo", uri: null, Args(new { server = "demo" }), context);

        authorizer.Requests.Should().ContainSingle();
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.resource.read");
        authorizer.Requests[0].Resource.Should().Be("demo|*");
    }

    [Fact]
    public async Task ResourceGate_DoesNot_Share_Execute_Kind()
    {
        var authorizer = new FakePermissionAuthorizer
        {
            DecisionFactory = req => req.PermissionKind == "mcp.execute"
                ? PermissionEffect.Allow
                : PermissionEffect.Deny
        };
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        var resolution = await McpResourcePermissionGate.AuthorizeAsync(
            "demo", "res://x", Args(new { }), context);

        resolution!.Decision.Should().Be(PermissionEffect.Deny);
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.resource.read");
    }

    [Fact]
    public async Task ListResources_When_NotSupported_Returns_Clear_Failure()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, false, false, false, false, false, false, false));
        var manager = CreateManager(session);
        var tool = new McpListResourcesTool(NullLogger<McpListResourcesTool>.Instance, manager);
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = new FakePermissionAuthorizer() };

        var result = await tool.ExecuteAsync(Args(new { server = "demo" }), context);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error!.Should().Match(s =>
            s.Contains("不支持", StringComparison.Ordinal) ||
            s.Contains("resources", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("capability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListResources_When_Supported_Returns_Items()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, true, false, false, false, false, false, false))
        {
            ListResourcesResult = new McpResourceListResult(
            [
                new McpResourceInfo("res://a", "a", "desc-a", "text/plain")
            ])
        };
        var manager = CreateManager(session);
        var tool = new McpListResourcesTool(NullLogger<McpListResourcesTool>.Instance, manager);
        var context = new ToolContext
        {
            SessionId = "s1",
            PermissionAuthorizer = new FakePermissionAuthorizer()
        };

        var result = await tool.ExecuteAsync(Args(new { server = "demo" }), context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("res://a");
        result.Output.Should().Contain("a");
    }

    [Fact]
    public async Task ReadResource_Permission_Key_Is_ServerPipeUri()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, true, false, false, false, false, false, false))
        {
            ReadResourceResult = new McpResourceReadResult(
            [
                new McpResourceContent("res://doc", "text/plain", Text: "hello")
            ])
        };
        var manager = CreateManager(session);
        var tool = new McpReadResourceTool(NullLogger<McpReadResourceTool>.Instance, manager);
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        await tool.ExecuteAsync(Args(new { server = "demo", uri = "res://doc" }), context);

        authorizer.Requests.Should().ContainSingle();
        authorizer.Requests[0].Resource.Should().Be("demo|res://doc");
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.resource.read");
    }

    [Fact]
    public async Task ReadResource_Binary_Is_Summarized_Not_Dumped()
    {
        var blob = Convert.ToBase64String(new byte[1200]);
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, true, false, false, false, false, false, false))
        {
            ReadResourceResult = new McpResourceReadResult(
            [
                new McpResourceContent("res://bin", "application/octet-stream", BlobBase64: blob)
            ])
        };
        var manager = CreateManager(session);
        var tool = new McpReadResourceTool(NullLogger<McpReadResourceTool>.Instance, manager);
        var context = new ToolContext
        {
            SessionId = "s1",
            PermissionAuthorizer = new FakePermissionAuthorizer()
        };

        var result = await tool.ExecuteAsync(Args(new { server = "demo", uri = "res://bin" }), context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("application/octet-stream");
        result.Output.Should().NotContain(blob);
        result.Output.Should().Match(s =>
            s.Contains("字节", StringComparison.Ordinal) ||
            s.Contains("binary", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("摘要", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListResourceTemplates_When_NotSupported_Returns_Clear_Failure()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, false, false, false, false, false, false, false));
        var manager = CreateManager(session);
        var tool = new McpListResourceTemplatesTool(
            NullLogger<McpListResourceTemplatesTool>.Instance, manager);
        var context = new ToolContext
        {
            SessionId = "s1",
            PermissionAuthorizer = new FakePermissionAuthorizer()
        };

        var result = await tool.ExecuteAsync(Args(new { server = "demo" }), context);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Module_ProvidedTools_Contains_Resource_Tool_Ids()
    {
        var module = new McpModule();
        module.ProvidedTools.Should().Contain("mcp_list_resources");
        module.ProvidedTools.Should().Contain("mcp_read_resource");
        module.ProvidedTools.Should().Contain("mcp_list_resource_templates");
    }

    private static IMcpManager CreateManager(IMcpSession session)
    {
        var mock = new Mock<IMcpManager>();
        mock.Setup(m => m.TryGetSession("demo")).Returns(session);
        mock.Setup(m => m.IsAvailable("demo")).Returns(true);
        return mock.Object;
    }

    private sealed class FakePermissionAuthorizer : IPermissionAuthorizer
    {
        public string SessionId => "s1";
        public List<PermissionRequest> Requests { get; } = new();
        public PermissionEffect Decision { get; set; } = PermissionEffect.Allow;
        public Func<PermissionRequest, PermissionEffect>? DecisionFactory { get; set; }

        public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var decision = DecisionFactory?.Invoke(request) ?? Decision;
            return Task.FromResult(new PermissionResolution
            {
                RequestId = "req-1",
                SessionId = request.SessionId,
                Decision = decision,
                ResolvedBy = PermissionResolvedBy.User
            });
        }
    }

    private sealed class FakeMcpSession : IMcpSession
    {
        public FakeMcpSession(McpServerCapabilitiesInfo capabilities) => Capabilities = capabilities;

        public string ServerName => "demo";
        public McpServerCapabilitiesInfo Capabilities { get; }

        public McpResourceListResult ListResourcesResult { get; set; } =
            McpResourceListResult.Unsupported("resources not declared");

        public McpResourceReadResult ReadResourceResult { get; set; } =
            McpResourceReadResult.Unsupported("resources not declared");

        public McpResourceTemplateListResult ListTemplatesResult { get; set; } =
            McpResourceTemplateListResult.Unsupported("resources not declared");

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpSessionToolInfo>>(Array.Empty<McpSessionToolInfo>());

        public Task<McpSessionToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpSessionToolCallResult(false, ""));

        public Task<McpResourceListResult> ListResourcesAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Resources)
                return Task.FromResult(McpResourceListResult.Unsupported("server 未声明 resources capability"));
            return Task.FromResult(ListResourcesResult);
        }

        public Task<McpResourceReadResult> ReadResourceAsync(
            string uri, CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Resources)
                return Task.FromResult(McpResourceReadResult.Unsupported("server 未声明 resources capability"));
            return Task.FromResult(ReadResourceResult);
        }

        public Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Resources)
                return Task.FromResult(McpResourceTemplateListResult.Unsupported("server 未声明 resources capability"));
            return Task.FromResult(ListTemplatesResult);
        }

        public Task<McpPromptListResult> ListPromptsAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
            => Task.FromResult(McpPromptListResult.Unsupported());

        public Task<McpPromptGetResult> GetPromptAsync(
            string name,
            IReadOnlyDictionary<string, object?>? arguments = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(McpPromptGetResult.Unsupported());

        public Task<McpSkillExtListResult> ListSkillsAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
            => Task.FromResult(McpSkillExtListResult.Unsupported());

        public Task<McpSkillExtGetResult> GetSkillAsync(
            string uri, CancellationToken cancellationToken = default)
            => Task.FromResult(McpSkillExtGetResult.Unsupported());
    }
}
