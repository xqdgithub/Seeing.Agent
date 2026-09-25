using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Decorators;
using Seeing.Agent.Mcp;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// C5 安全回归：配置全局 <see cref="SeeingAgentOptions.ToolExecutionTimeout"/>（超时装饰器介入）后，
/// MCP 工具仍须收到 PermissionAuthorizer 并触发 <c>mcp.execute</c> 审批——不得因上下文重建丢字段而零审批。
/// </summary>
public class McpToolTimeoutApprovalTests
{
    private sealed class CapturingAuthorizer : IPermissionAuthorizer
    {
        public string SessionId => "s1";
        public List<PermissionRequest> Requests { get; } = new();

        public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new PermissionResolution
            {
                RequestId = "req",
                SessionId = request.SessionId,
                Decision = PermissionEffect.Allow,
                ResolvedBy = PermissionResolvedBy.User
            });
        }
    }

    private static McpTool CreateEchoTool()
    {
        var schema = JsonDocument.Parse("{}").RootElement;
        return new McpTool(
            "serverA",
            "do_thing",
            "回显",
            schema,
            (_, _, _) => Task.FromResult(new McpToolResult { Content = "ok" }));
    }

    private static ToolTimeoutDecorator CreateDecorator(ITool inner, TimeSpan? globalTimeout)
    {
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue)
            .Returns(new SeeingAgentOptions { ToolExecutionTimeout = globalTimeout });

        return new ToolTimeoutDecorator(inner, options.Object, NullLogger<ToolTimeoutDecorator>.Instance);
    }

    [Fact]
    public async Task McpTool_UnderGlobalTimeout_ShouldStillRequestApproval()
    {
        var authorizer = new CapturingAuthorizer();
        var decorator = CreateDecorator(CreateEchoTool(), TimeSpan.FromSeconds(30));
        var context = new ToolContext
        {
            SessionId = "s1",
            PermissionAuthorizer = authorizer,
            CancellationToken = CancellationToken.None
        };

        var result = await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, context);

        authorizer.Requests.Should().ContainSingle();
        authorizer.Requests[0].PermissionKind.Should().Be("mcp.execute");
        authorizer.Requests[0].Resource.Should().Be("serverA");
        result.Success.Should().BeTrue();
    }
}
