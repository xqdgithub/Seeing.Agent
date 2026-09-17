using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Mapping;
using Xunit;

namespace Seeing.Gateway.Tests.Mapping;

/// <summary>
/// G4.1：权限事件 → 协议帧映射契约（CamelCase 枚举串、AllowedScopes、无旧 PermissionResponse 残留）。
/// </summary>
public class PermissionEventMappingTests
{
    private const string SessionId = "ses_perm";

    [Fact]
    public void ChatEventMapper_PermissionRequest_ShouldMapAllowedScopes()
    {
        var evt = new PermissionRequestEvent
        {
            SessionId = SessionId,
            LoopId = "loop_1",
            RequestId = "req_1",
            CallId = "call_1",
            PermissionKind = "filesystem.write",
            Resource = "/tmp/a.txt",
            Arguments = new { path = "/tmp/a.txt" },
            Message = "写入需要确认",
            RiskLevel = "high",
            AllowedScopes =
            [
                PermissionGrantScope.Once,
                PermissionGrantScope.Session,
                PermissionGrantScope.SessionDirectory
            ]
        };

        var ok = ChatEventMapper.TryMap(evt, out var gatewayEvent);

        ok.Should().BeTrue();
        gatewayEvent!.Data!.PermissionId.Should().Be("req_1");
        gatewayEvent.Data.CallId.Should().Be("call_1");
        gatewayEvent.Data.PermissionKind.Should().Be("filesystem.write");
        gatewayEvent.Data.PermissionMessage.Should().Be("写入需要确认");
        gatewayEvent.Data.RiskLevel.Should().Be("high");
        gatewayEvent.Data.PermissionAllowedScopes.Should().Equal("once", "session", "sessionDirectory");
    }

    [Fact]
    public void GatewayEventMapper_PermissionRequest_ShouldMapAllowedScopes()
    {
        var evt = new PermissionRequestEvent
        {
            SessionId = SessionId,
            LoopId = "loop_1",
            RequestId = "req_1",
            CallId = "call_1",
            PermissionKind = "filesystem.write",
            Resource = "/tmp/a.txt",
            AllowedScopes = [PermissionGrantScope.Once, PermissionGrantScope.Session]
        };

        var result = GatewayEventMapper.Map(evt);

        result.Data!.PermissionAllowedScopes.Should().Equal("once", "session");
    }

    [Fact]
    public void ChatEventMapper_PermissionResolved_SessionDirectory_ShouldMapCamelCaseScope()
    {
        var evt = new PermissionResolvedEvent
        {
            SessionId = SessionId,
            LoopId = "loop_1",
            RequestId = "req_1",
            CallId = "call_1",
            Decision = PermissionEffect.Allow,
            Scope = PermissionGrantScope.SessionDirectory,
            ResolvedBy = PermissionResolvedBy.User
        };

        var ok = ChatEventMapper.TryMap(evt, out var gatewayEvent);

        ok.Should().BeTrue();
        gatewayEvent!.SourceType.Should().Be("permission_resolved");
        gatewayEvent.SourceType.Should().NotBe("permission_response");
        gatewayEvent.Data!.PermissionScope.Should().Be("sessionDirectory");
    }

    [Fact]
    public void ChatEventMapper_PermissionResolved_NoChannel_ShouldMapCamelCaseResolvedBy()
    {
        var evt = new PermissionResolvedEvent
        {
            SessionId = SessionId,
            RequestId = "req_1",
            Decision = PermissionEffect.Deny,
            Scope = PermissionGrantScope.Once,
            ResolvedBy = PermissionResolvedBy.NoChannel,
            Reason = "无交互通道"
        };

        var ok = ChatEventMapper.TryMap(evt, out var gatewayEvent);

        ok.Should().BeTrue();
        gatewayEvent!.Data!.PermissionResolvedBy.Should().Be("noChannel");
    }

    [Fact]
    public void GatewayEventMapper_PermissionResolved_SessionDirectory_ShouldMapCamelCaseScope()
    {
        var evt = new PermissionResolvedEvent
        {
            SessionId = SessionId,
            RequestId = "req_1",
            Decision = PermissionEffect.Allow,
            Scope = PermissionGrantScope.SessionDirectory,
            ResolvedBy = PermissionResolvedBy.Policy
        };

        var result = GatewayEventMapper.Map(evt);

        result.SourceType.Should().Be(MessageEventType.PermissionResolved);
        result.SourceType.Should().NotBe("permission_response");
        result.Data!.PermissionScope.Should().Be("sessionDirectory");
        result.Data.PermissionResolvedBy.Should().Be("policy");
    }

    [Fact]
    public void GatewayEventMapper_PermissionResolved_NoChannel_ShouldMapCamelCaseResolvedBy()
    {
        var evt = new PermissionResolvedEvent
        {
            SessionId = SessionId,
            RequestId = "req_1",
            Decision = PermissionEffect.Deny,
            Scope = PermissionGrantScope.Once,
            ResolvedBy = PermissionResolvedBy.NoChannel
        };

        var result = GatewayEventMapper.Map(evt);

        result.Data!.PermissionResolvedBy.Should().Be("noChannel");
    }

    [Fact]
    public void GatewayEventMapper_PermissionResolved_Cancellation_ShouldRemainLowercase()
    {
        var evt = new PermissionResolvedEvent
        {
            SessionId = SessionId,
            RequestId = "req_1",
            Decision = PermissionEffect.Deny,
            Scope = PermissionGrantScope.Once,
            ResolvedBy = PermissionResolvedBy.Cancellation
        };

        var result = GatewayEventMapper.Map(evt);

        result.Data!.PermissionResolvedBy.Should().Be("cancellation");
    }

    [Fact]
    public void MessageEventType_ShouldExposePermissionResolvedAndNotLegacyPermissionResponse()
    {
        MessageEventType.PermissionRequest.Should().Be("permission.request");
        MessageEventType.PermissionResolved.Should().Be("permission.resolved");
        typeof(MessageEventType).GetField("PermissionResponse").Should().BeNull();
        typeof(MessageEventType).GetField("PermissionResponseLegacy").Should().BeNull();
    }
}
