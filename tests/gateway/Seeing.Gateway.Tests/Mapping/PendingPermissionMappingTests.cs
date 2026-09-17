using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.Tests.Mapping;

/// <summary>
/// G4.2：pending 端点载荷投影：GatewayPendingPermission 全字段 → Permission 事件帧。
/// </summary>
public class PendingPermissionMappingTests
{
    private const string SessionId = "ses_pending";

    [Fact]
    public void MapPendingPermission_ShouldProjectAllFields()
    {
        var createdAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var pending = new GatewayPendingPermission
        {
            PermissionId = "req_1",
            SessionId = SessionId,
            LoopId = "loop_1",
            PermissionKind = "filesystem.write",
            Resource = "/tmp/a.txt",
            Arguments = new { path = "/tmp/a.txt" },
            Message = "写入需要确认",
            RiskLevel = "high",
            CreatedAt = createdAt
        };

        var result = GatewayEventMapper.MapPendingPermission(pending);

        result.Object.Should().Be(GatewayEventObject.Permission);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.SessionId.Should().Be(SessionId);
        result.LoopId.Should().Be("loop_1");
        result.Timestamp.Should().Be(createdAt);
        result.SourceType.Should().Be(MessageEventType.PermissionRequest);
        result.Data!.PermissionId.Should().Be("req_1");
        result.Data.PermissionKind.Should().Be("filesystem.write");
        result.Data.Resource.Should().Be("/tmp/a.txt");
        result.Data.PermissionArguments.Should().BeSameAs(pending.Arguments);
        result.Data.PermissionMessage.Should().Be("写入需要确认");
        result.Data.RiskLevel.Should().Be("high");
    }
}
