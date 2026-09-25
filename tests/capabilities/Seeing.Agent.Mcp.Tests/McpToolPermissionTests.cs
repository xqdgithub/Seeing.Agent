using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Mcp;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// 批次 1.4：MCP 工具 server 粒度资源门。
/// 工具以 <c>mcp.execute</c> kind、resource=server 名发起审批。
/// </summary>
public class McpToolPermissionTests
{
    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task AuthorizeAsync_FirstCall_ShouldRequestServerScopedPermission()
    {
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = "s1", CallId = "call-1", PermissionAuthorizer = authorizer };

        var resolution = await McpToolPermissionGate.AuthorizeAsync("serverA", "do_thing", EmptyArgs(), context);

        resolution.Should().NotBeNull();
        authorizer.Requests.Should().ContainSingle();
        var request = authorizer.Requests[0];
        request.PermissionKind.Should().Be("mcp.execute");
        request.Resource.Should().Be("serverA");
        request.SessionId.Should().Be("s1");
        request.CallId.Should().Be("call-1");
        request.Metadata["server"].Should().Be("serverA");
        request.Metadata["tool"].Should().Be("do_thing");
    }

    [Fact]
    public async Task AuthorizeAsync_NoAuthorizer_ShouldReturnNull()
    {
        var context = new ToolContext { SessionId = "s1" };

        var resolution = await McpToolPermissionGate.AuthorizeAsync("serverA", "do_thing", EmptyArgs(), context);

        resolution.Should().BeNull();
    }

    [Fact]
    public async Task AuthorizeAsync_Denied_ShouldReturnDenyResolution()
    {
        var authorizer = new FakePermissionAuthorizer { Decision = PermissionEffect.Deny, Reason = "用户拒绝" };
        var context = new ToolContext { SessionId = "s1", PermissionAuthorizer = authorizer };

        var resolution = await McpToolPermissionGate.AuthorizeAsync("serverA", "do_thing", EmptyArgs(), context);

        resolution.Should().NotBeNull();
        resolution!.Decision.Should().Be(PermissionEffect.Deny);
        resolution.Reason.Should().Be("用户拒绝");
    }

    [Fact]
    public async Task AuthorizeAsync_EmptySessionId_ShouldFallbackToAuthorizerSession()
    {
        var authorizer = new FakePermissionAuthorizer();
        var context = new ToolContext { SessionId = string.Empty, PermissionAuthorizer = authorizer };

        await McpToolPermissionGate.AuthorizeAsync("serverA", "do_thing", EmptyArgs(), context);

        authorizer.Requests[0].SessionId.Should().Be("s1");
    }

    private sealed class FakePermissionAuthorizer : IPermissionAuthorizer
    {
        public string SessionId => "s1";

        public List<PermissionRequest> Requests { get; } = new();

        public PermissionEffect Decision { get; set; } = PermissionEffect.Allow;

        public string? Reason { get; set; }

        public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new PermissionResolution
            {
                RequestId = "req-1",
                SessionId = request.SessionId,
                Decision = Decision,
                ResolvedBy = PermissionResolvedBy.User,
                Reason = Reason
            });
        }
    }
}
