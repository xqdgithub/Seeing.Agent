using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 授权引擎决策链（spec §5.1 步骤 0-8）。Manager / Presenter / Channel 用 Moq 替身。
/// </summary>
public class PermissionAuthorizationTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "seeing-auth-tests");
    private static readonly string InsidePath = Path.Combine(Root, "inside.txt");
    private static readonly string OutsideDir = Path.Combine(Path.GetTempPath(), "seeing-auth-outside");
    private static readonly string OutsidePath = Path.Combine(OutsideDir, "outside.txt");
    private static readonly string WhitelistDir = Path.Combine(Root, "whitelisted");

    // === 步骤 1：工作区边界 ===

    [Fact]
    public async Task AuthorizeAsync_InsideWorkspace_ShouldAllowWithoutAsk()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", InsidePath));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_InsideWhitelist_ShouldAllowWithoutAsk()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.Store.AddSessionDirectory("s1", WhitelistDir);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_NoSessionId_WithRestrict_ShouldDenyPolicy()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", InsidePath, sessionId: string.Empty));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        resolution.Reason.Should().Contain("会话");
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_OutsideWorkspace_NoPresence_ShouldDenyNoChannel()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", OutsidePath));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_OutsideWorkspace_ApprovedOnce_ShouldWhitelistDirectoryAndAllow()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.write", OutsidePath));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.Store.ContainsSessionPath("s1", OutsidePath).Should().BeTrue();
        h.Gate.EnsureAllowed("s1", OutsidePath).Should().BeNull();
    }

    [Fact]
    public async Task AuthorizeAsync_BoundaryAllow_ShouldBeatRuleDeny()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetAgentPolicy("agent", PermissionRuleEntry.Deny(PermissionKind.File, "*"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", InsidePath, agentName: "agent"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    // === 步骤 2：授权记忆 ===

    [Fact]
    public async Task AuthorizeAsync_MemoryDeny_ShouldShortCircuit()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("filesystem.read", "secret.pem", PermissionGrantScope.Session, PermissionEffect.Deny));

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", "secret.pem"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_MemoryAllow_ShouldShortCircuit()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("filesystem.read", "secret.pem", PermissionGrantScope.Session, PermissionEffect.Allow));

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", "secret.pem"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_MemoryDirectoryPrefix_ShouldMatch()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("filesystem.read", WhitelistDir, PermissionGrantScope.SessionDirectory, PermissionEffect.Allow));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "nested", "a.txt")));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_SessionScopeMemory_SubPath_ShouldNotShortCircuit()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("filesystem.read", WhitelistDir, PermissionGrantScope.Session, PermissionEffect.Allow));
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertAskedOnce();
    }

    [Fact]
    public async Task AuthorizeAsync_MemoryDeny_ShouldBeatRuleAllow()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("filesystem.read", "secret.pem", PermissionGrantScope.Session, PermissionEffect.Deny));
        h.SetAgentPolicy("agent", PermissionRuleEntry.Allow(PermissionKind.File, "*"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", "secret.pem", agentName: "agent"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        h.AssertNoAsk();
    }

    // === 步骤 3：Agent 规则 + PermissionKindMapper ===

    [Fact]
    public async Task AuthorizeAsync_RuleDeny_WithKindMapper_ShouldDenyPolicy()
    {
        var h = new Harness();
        h.SetAgentPolicy("agent", PermissionRuleEntry.Deny(PermissionKind.File, "*.pem"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", "secret.pem", agentName: "agent"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_RuleAllow_WithKindMapper_ShouldAllowWithoutAsk()
    {
        // 能力门（非资源类 kind）：Allow 规则仍短路放行。
        var h = new Harness();
        h.SetAgentPolicy("agent", PermissionRuleEntry.Allow(PermissionKind.Tool, "ba*"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("tool.execute", "bash", agentName: "agent"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    // F1：资源类 kind 的 Allow 规则不得短路询问（旧 EvaluateFileAsync 死代码语义）。
    [Fact]
    public async Task AuthorizeAsync_ResourceKind_RuleAllow_OutsideWorkspace_ShouldNotShortCircuit()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetAgentPolicy("agent", PermissionRuleEntry.Allow(PermissionKind.File, "*"));
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", OutsidePath, agentName: "agent"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_ToolKind_RuleAllow_ShouldStillShortCircuit()
    {
        var h = new Harness();
        h.SetAgentPolicy("agent", PermissionRuleEntry.Allow(PermissionKind.Tool, "*"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("tool.execute", "bash", agentName: "agent"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_ResourceKind_RuleDeny_ShouldDeny()
    {
        var h = new Harness();
        h.SetAgentPolicy("agent", PermissionRuleEntry.Deny(PermissionKind.Shell, "*"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("shell.execute", "bash -c rm -rf /", agentName: "agent"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    // F4：Agent 策略的"默认效果 Deny"不是显式 Deny 规则，不得硬拒资源类 kind。
    // 无匹配规则时应进入询问（release notes 3.5「资源门仅应用 Deny 规则」；子代理 explore 场景）。
    [Fact]
    public async Task AuthorizeAsync_ResourceKind_DefaultEffectDeny_NoMatchingRule_ShouldAsk()
    {
        var h = new Harness();
        h.SetAgentPolicy("explore", PermissionEffect.Deny,
            PermissionRuleEntry.Allow(PermissionKind.Tool, "read"));
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", OutsidePath, agentName: "explore"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        h.AssertAskedOnce();
    }

    // F4：非资源类 kind 的默认效果 Deny 仍应硬拒（保持原语义，不受 F4 修复影响）。
    [Fact]
    public async Task AuthorizeAsync_NonResourceKind_DefaultEffectDeny_NoMatchingRule_ShouldDeny()
    {
        var h = new Harness();
        h.SetAgentPolicy("explore", PermissionEffect.Deny,
            PermissionRuleEntry.Allow(PermissionKind.Tool, "read"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("tool.execute", "git_status", agentName: "explore"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_RuleAllow_RequireInteraction_ShouldStillAsk()
    {
        var h = new Harness();
        h.SetAgentPolicy("agent", PermissionRuleEntry.Allow(PermissionKind.Tool, "*"));
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("tool.execute", "bash", agentName: "agent", requireInteraction: true));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertAskedOnce();
    }

    [Fact]
    public async Task AuthorizeAsync_NoAgentName_ShouldSkipRuleStep_AndAsk()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Deny, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        h.AssertAskedOnce();
    }

    // === 步骤 4：生效开关（实时） ===

    [Fact]
    public async Task AuthorizeAsync_OverrideEnabled_ShouldAllow()
    {
        var h = new Harness();
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("tool.execute", "bash", @override: SessionAutoApprove.Enabled));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_SessionEnabled_ShouldAllow()
    {
        var h = new Harness();
        h.Sessions.Setup(s => s.Get("s1")).Returns(new SessionData { Id = "s1", AutoApprove = SessionAutoApprove.Enabled });

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_GlobalAutoApproveAll_ShouldAllow()
    {
        var h = new Harness();
        h.Options.Permission.AutoApproveAll = true;

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_GlobalAutoApproveAll_RequireInteraction_ShouldDenyNoChannel()
    {
        var h = new Harness();
        h.Options.Permission.AutoApproveAll = true;
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("tool.execute", "bash", requireInteraction: true));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_SessionDisabled_ShouldSkipChannelAutoApproveAndAsk()
    {
        var h = new Harness();
        h.Sessions.Setup(s => s.Get("s1")).Returns(new SessionData { Id = "s1", AutoApprove = SessionAutoApprove.Disabled });
        h.AddAutoApproveChannel();
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertAskedOnce();
    }

    // F2：RequireInteraction=true 必须短路宿主通道自动批准（spec §5.1）。
    [Fact]
    public async Task AuthorizeAsync_RequireInteraction_ShouldSkipChannelAutoApprove()
    {
        var h = new Harness();
        h.AddAutoApproveChannel();
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("tool.execute", "bash", requireInteraction: true));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        h.AssertNoAsk();
    }

    // === 步骤 5：宿主通道 ===

    [Fact]
    public async Task AuthorizeAsync_ChannelAutoApprove_ShouldAllowWithoutAsk()
    {
        var h = new Harness();
        h.AddAutoApproveChannel();

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_DenyAllChannel_ShouldFallThroughToAsk()
    {
        var h = new Harness();
        h.Channels.Add(new DenyAllPermissionChannel());
        h.SetupAsk(PermissionEffect.Deny, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        h.AssertAskedOnce();
    }

    // === 步骤 6：询问 ===

    [Fact]
    public async Task AuthorizeAsync_NoPresenter_ShouldDenyNoChannel()
    {
        var h = new Harness();
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        resolution.Reason.Should().Contain("无交互");
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_CanSurface_ShouldPresentToChannelsAndWait()
    {
        var h = new Harness();
        var channel = new Mock<IPermissionChannel>();
        h.Channels.Add(channel.Object);
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        channel.Verify(c => c.PresentAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Manager.Verify(m => m.WaitAsync(It.IsAny<PermissionTicket>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AuthorizeAsync_TwoGatesSameCallId_ShouldNotMerge()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash", callId: "c1"));
        await h.Service.AuthorizeAsync(h.Request("filesystem.write", OutsidePath, callId: "c1"));

        h.Manager.Verify(
            m => m.BeginAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // === 步骤 7：决策后处理 ===

    [Fact]
    public async Task AuthorizeAsync_AllowSessionScope_ShouldPersistGrantAndShortCircuitSecond()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Session);

        var first = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));
        first.Decision.Should().Be(PermissionEffect.Allow);
        h.Store.Lookup("s1", "tool.execute", "bash").Should().ContainSingle()
            .Which.Scope.Should().Be(PermissionGrantScope.Session);

        h.Manager.Invocations.Clear();
        var second = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        second.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_DenySessionScope_ShouldPersistDenyGrant()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Deny, PermissionGrantScope.Session);

        await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"));

        h.Store.Lookup("s1", "tool.execute", "bash").Should().ContainSingle()
            .Which.Effect.Should().Be(PermissionEffect.Deny);
    }

    // F3：SessionDirectory 作用域须以目录前缀写入，兄弟文件可经 Lookup 命中。
    [Fact]
    public async Task AuthorizeAsync_AllowSessionDirectoryScope_ShouldPersistDirectoryPrefix()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.SessionDirectory);

        await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")));

        h.Store.Lookup("s1", "filesystem.read", Path.Combine(WhitelistDir, "b.txt"))
            .Should().ContainSingle()
            .Which.Scope.Should().Be(PermissionGrantScope.SessionDirectory);
    }

    // F3：Session 作用域仍为精确匹配，不因写目录前缀而放大命中范围。
    [Fact]
    public async Task AuthorizeAsync_AllowSessionScope_FileResource_ShouldStayExact()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Session);

        await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")));

        h.Store.Lookup("s1", "filesystem.read", Path.Combine(WhitelistDir, "a.txt"))
            .Should().ContainSingle().Which.Scope.Should().Be(PermissionGrantScope.Session);
        h.Store.Lookup("s1", "filesystem.read", Path.Combine(WhitelistDir, "b.txt"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task AuthorizeAsync_ShouldAssignRequestId()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", InsidePath));

        resolution.RequestId.Should().NotBeNullOrEmpty();
    }

    // === AllowedScopes 按 kind 注入（ExecutionContextPermissionAuthorizer） ===

    [Fact]
    public async Task ExecutionContextPermissionAuthorizer_FilesystemKind_ShouldInjectDirectoryScope()
    {
        var service = new Mock<IPermissionService>();
        PermissionRequest? captured = null;
        service.Setup(s => s.AuthorizeAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PermissionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new PermissionResolution
            {
                RequestId = "x",
                SessionId = "s1",
                Decision = PermissionEffect.Allow,
                ResolvedBy = PermissionResolvedBy.Policy
            });
        var authorizer = new ExecutionContextPermissionAuthorizer(service.Object, "s1", SessionAutoApprove.Enabled);

        await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = string.Empty,
            PermissionKind = "filesystem.write",
            Resource = "x"
        });

        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be("s1");
        captured.Override.Should().Be(SessionAutoApprove.Enabled);
        captured.AllowedScopes.Should().BeEquivalentTo(
            new[] { PermissionGrantScope.Once, PermissionGrantScope.Session, PermissionGrantScope.SessionDirectory });
    }

    [Fact]
    public async Task ExecutionContextPermissionAuthorizer_NonFilesystemKind_ShouldInjectTwoScopes()
    {
        var service = new Mock<IPermissionService>();
        PermissionRequest? captured = null;
        service.Setup(s => s.AuthorizeAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PermissionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new PermissionResolution
            {
                RequestId = "x",
                SessionId = "s1",
                Decision = PermissionEffect.Allow,
                ResolvedBy = PermissionResolvedBy.Policy
            });
        var authorizer = new ExecutionContextPermissionAuthorizer(service.Object, "s1");

        await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            Resource = "bash"
        });

        captured!.AllowedScopes.Should().BeEquivalentTo(
            new[] { PermissionGrantScope.Once, PermissionGrantScope.Session });
    }

    // === 测试夹具 ===

    private sealed class Harness
    {
        public PermissionGrantStore Store { get; } = new();
        public Mock<IPermissionRequestManager> Manager { get; } = new();
        public Mock<IPermissionPresentationStore> Presentation { get; } = new();
        public List<IPermissionChannel> Channels { get; } = new();
        public Mock<IAgentRegistry> Registry { get; } = new();
        public Mock<ISessionManager> Sessions { get; } = new();
        public SeeingAgentOptions Options { get; } = new();
        public WorkspacePathGate Gate { get; }
        public PermissionService Service { get; }

        public Harness()
        {
            Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(true);

            var options = OptionsMonitor(Options);
            var workspace = new Mock<IWorkspaceProvider>();
            workspace.SetupGet(w => w.ProjectSeeingDirectory).Returns(Path.Combine(Root, ".seeing"));

            Gate = new WorkspacePathGate(workspace.Object, Store, options);
            var effective = new EffectivePermissionPolicy(Sessions.Object, options);
            Service = new PermissionService(
                NullLogger<PermissionService>.Instance,
                Store,
                effective,
                Gate,
                options,
                Manager.Object,
                Presentation.Object,
                Channels,
                Registry.Object);
        }

        public PermissionRequest Request(
            string kind,
            string? resource,
            string sessionId = "s1",
            string? agentName = null,
            string? callId = null,
            bool requireInteraction = false,
            SessionAutoApprove? @override = null) => new()
        {
            SessionId = sessionId,
            CallId = callId,
            AgentName = agentName,
            PermissionKind = kind,
            Resource = resource,
            RequireInteraction = requireInteraction,
            Override = @override
        };

        public void SetAgentPolicy(string agentName, params PermissionRuleEntry[] rules)
        {
            Registry.Setup(r => r.GetAgentAsync(agentName)).ReturnsAsync(new AgentDefinition
            {
                Name = agentName,
                PermissionRules = rules.ToList(),
                PermissionDefaultEffect = PermissionEffect.Ask
            });
        }

        public void SetAgentPolicy(string agentName, PermissionEffect defaultEffect, params PermissionRuleEntry[] rules)
        {
            Registry.Setup(r => r.GetAgentAsync(agentName)).ReturnsAsync(new AgentDefinition
            {
                Name = agentName,
                PermissionRules = rules.ToList(),
                PermissionDefaultEffect = defaultEffect
            });
        }

        public void AddAutoApproveChannel()
        {
            var channel = new Mock<IPermissionChannel>();
            channel.Setup(c => c.TryAutoApprove(It.IsAny<PermissionRequest>())).Returns(PermissionEffect.Allow);
            Channels.Add(channel.Object);
        }

        public void SetupAsk(
            PermissionEffect decision = PermissionEffect.Allow,
            PermissionGrantScope scope = PermissionGrantScope.Once)
        {
            Manager.Setup(m => m.BeginAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
                .Returns((PermissionRequest r, CancellationToken _) => Task.FromResult(new PermissionTicket(r.RequestId ?? "req", r.SessionId)));
            Manager.Setup(m => m.WaitAsync(It.IsAny<PermissionTicket>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PermissionResolution
                {
                    RequestId = "req",
                    SessionId = "s1",
                    Decision = decision,
                    Scope = scope,
                    ResolvedBy = PermissionResolvedBy.User,
                    Reason = "asked"
                });
        }

        public void AssertNoAsk() => Manager.Verify(
            m => m.BeginAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        public void AssertAskedOnce() => Manager.Verify(
            m => m.BeginAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static IOptionsMonitor<SeeingAgentOptions> OptionsMonitor(SeeingAgentOptions value)
    {
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(value);
        return options.Object;
    }
}
