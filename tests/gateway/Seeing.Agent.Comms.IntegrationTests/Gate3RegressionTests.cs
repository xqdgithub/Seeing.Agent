using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Mapping;
using Xunit;

namespace Seeing.Agent.Comms.IntegrationTests;

public class Gate3RegressionTests
{
    [Fact]
    public void LoopComplete_MapperThenCollector_IsRunCompleted()
    {
        GatewayEventMapper.TryMap(
            new LoopCompleteEvent { SessionId = "s", LoopId = "l", Success = true },
            out var gatewayEvent).Should().BeTrue();

        gatewayEvent!.SourceType.Should().Be(MessageEventType.LoopComplete);

        var disposition = new GatewayAssistantReplyCollector().Apply(gatewayEvent);
        disposition.Should().Be(GatewayReplyDisposition.RunCompleted);
    }

    [Fact]
    public void RetainedAdminAndHealthEndpoints_AreRegistered()
    {
        var root = FindRepoRoot();
        var gateway = File.ReadAllText(Path.Combine(root, "src", "gateway", "Seeing.Agent.Gateway", "Endpoints", "GatewayEndpoints.cs"));
        var admin = File.ReadAllText(Path.Combine(root, "src", "gateway", "Seeing.Agent.Gateway", "Endpoints", "AdminEndpoints.cs"));

        gateway.Should().Contain("app.MapGet(\"/api/gateway/health\"");
        admin.Should().Contain("app.MapGet(\"/api/admin/channels/connected\"");
        admin.Should().Contain("app.MapGet(\"/api/admin/status\"");
        admin.Should().Contain("app.MapGet(\"/api/admin/sessions\"");
        admin.Should().Contain("app.MapGet(\"/api/admin/jobs\"");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（Seeing.Agent.slnx）。");
    }
}
