using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Interactions;
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

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", InsidePath), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", InsidePath, sessionId: string.Empty), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        resolution.Reason.Should().Contain("会话");
        h.AssertNoAsk();
    }

    // 步骤 1 边界预检的 RequireInteraction 守卫（spec §5.1）：
    // 工作区内路径默认静默放行，但 RequireInteraction=true 时不被静默放行，落入后续询问。
    [Fact]
    public async Task AuthorizeAsync_InsideWorkspace_RequireInteraction_ShouldAsk()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", InsidePath, requireInteraction: true), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        h.AssertAskedOnce();
    }

    [Fact]
    public async Task AuthorizeAsync_OutsideWorkspace_NoPresence_ShouldDenyNoChannel()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", OutsidePath), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        h.AssertNoAsk();
    }

    // 7a 收紧（spec §1.2）：Once 批准不写会话白名单——单次批准不得放大为整目录免审。
    // 旧行为（无条件 AddSessionDirectory）已按维护者确认的破坏性收紧移除。
    [Fact]
    public async Task AuthorizeAsync_OutsideWorkspace_ApprovedOnce_ShouldNotWhitelistDirectory()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.write", OutsidePath), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.Store.ContainsSessionPath("s1", OutsidePath).Should().BeFalse();
        h.Gate.EnsureAllowed("s1", OutsidePath).Should().NotBeNull();
    }

    // 7a 收紧回归：Once 批准后，同目录第二次 write 仍询问（非免审）。
    [Fact]
    public async Task AuthorizeAsync_OutsideWorkspace_ApprovedOnce_SecondWriteSameDirectory_ShouldAskAgain()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var siblingPath = Path.Combine(OutsideDir, "sibling.txt");
        var first = await h.Service.AuthorizeAsync(h.Request("filesystem.write", OutsidePath), TestContext.Current.CancellationToken);
        first.Decision.Should().Be(PermissionEffect.Allow);

        var second = await h.Service.AuthorizeAsync(h.Request("filesystem.write", siblingPath), TestContext.Current.CancellationToken);

        second.Decision.Should().Be(PermissionEffect.Allow);
        second.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        h.AssertAskedTimes(2);
    }

    // 7a 保持语义：SessionDirectory 批准（Scope != Once）写入会话白名单，同目录后续访问由 gate 直接放行。
    [Fact]
    public async Task AuthorizeAsync_OutsideWorkspace_ApprovedSessionDirectory_ShouldWhitelistDirectory()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.SessionDirectory);

        var first = await h.Service.AuthorizeAsync(h.Request("filesystem.write", OutsidePath), TestContext.Current.CancellationToken);
        first.Decision.Should().Be(PermissionEffect.Allow);

        h.Store.ContainsSessionPath("s1", OutsidePath).Should().BeTrue();
        h.Gate.EnsureAllowed("s1", OutsidePath).Should().BeNull();

        h.Manager.Invocations.Clear();
        var siblingPath = Path.Combine(OutsideDir, "sibling.txt");
        var second = await h.Service.AuthorizeAsync(h.Request("filesystem.write", siblingPath), TestContext.Current.CancellationToken);

        second.Decision.Should().Be(PermissionEffect.Allow);
        second.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_BoundaryAllow_ShouldBeatRuleDeny()
    {
        var h = new Harness();
        h.Options.Workspace.RestrictToWorkspace = true;
        h.SetAgentPolicy("agent", PermissionRuleEntry.Deny(PermissionKind.File, "*"));

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("filesystem.read", InsidePath, agentName: "agent"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    // === 步骤 2：授权记忆 ===

    [Fact]
    public async Task AuthorizeAsync_MemoryDeny_ShouldShortCircuit()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("filesystem.read", "secret.pem", PermissionGrantScope.Session, PermissionEffect.Deny));

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", "secret.pem"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_MemoryAllow_ShouldShortCircuit()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("filesystem.read", "secret.pem", PermissionGrantScope.Session, PermissionEffect.Allow));

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", "secret.pem"), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "nested", "a.txt")), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", "secret.pem", agentName: "agent"), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", "secret.pem", agentName: "agent"), TestContext.Current.CancellationToken);

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
            h.Request("tool.execute", "bash", agentName: "agent"), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", OutsidePath, agentName: "agent"), TestContext.Current.CancellationToken);

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
            h.Request("tool.execute", "bash", agentName: "agent"), TestContext.Current.CancellationToken);

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
            h.Request("shell.execute", "bash -c rm -rf /", agentName: "agent"), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", OutsidePath, agentName: "explore"), TestContext.Current.CancellationToken);

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
            h.Request("tool.execute", "git_status", agentName: "explore"), TestContext.Current.CancellationToken);

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
            h.Request("tool.execute", "bash", agentName: "agent", requireInteraction: true), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertAskedOnce();
    }

    [Fact]
    public async Task AuthorizeAsync_NoAgentName_ShouldSkipRuleStep_AndAsk()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Deny, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

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
            h.Request("tool.execute", "bash", @override: SessionAutoApprove.Enabled), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_SessionEnabled_ShouldAllow()
    {
        var h = new Harness();
        h.Sessions.Setup(s => s.Get("s1")).Returns(new SessionData { Id = "s1", AutoApprove = SessionAutoApprove.Enabled });

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_GlobalAutoApproveAll_ShouldAllow()
    {
        var h = new Harness();
        h.Options.Permission.AutoApproveAll = true;

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

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
            h.Request("tool.execute", "bash", requireInteraction: true), TestContext.Current.CancellationToken);

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

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

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
            h.Request("tool.execute", "bash", requireInteraction: true), TestContext.Current.CancellationToken);

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

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

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

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        h.AssertAskedOnce();
    }

    // === 步骤 6：询问 ===

    [Fact]
    public async Task AuthorizeAsync_NoPresenter_ShouldDenyNoChannel()
    {
        var h = new Harness();
        h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);

        var resolution = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

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

        await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

        channel.Verify(c => c.PresentAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Manager.Verify(m => m.WaitAsync(It.IsAny<RequestTicket>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AuthorizeAsync_TwoGatesSameCallId_ShouldNotMerge()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash", callId: "c1"), TestContext.Current.CancellationToken);
        await h.Service.AuthorizeAsync(h.Request("filesystem.write", OutsidePath, callId: "c1"), TestContext.Current.CancellationToken);

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

        var first = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);
        first.Decision.Should().Be(PermissionEffect.Allow);
        h.Store.Lookup("s1", "tool.execute", "bash").Should().ContainSingle()
            .Which.Scope.Should().Be(PermissionGrantScope.Session);

        h.Manager.Invocations.Clear();
        var second = await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

        second.Decision.Should().Be(PermissionEffect.Allow);
        h.AssertNoAsk();
    }

    [Fact]
    public async Task AuthorizeAsync_DenySessionScope_ShouldPersistDenyGrant()
    {
        var h = new Harness();
        h.SetupAsk(PermissionEffect.Deny, PermissionGrantScope.Session);

        await h.Service.AuthorizeAsync(h.Request("tool.execute", "bash"), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")), TestContext.Current.CancellationToken);

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
            h.Request("filesystem.read", Path.Combine(WhitelistDir, "a.txt")), TestContext.Current.CancellationToken);

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

        var resolution = await h.Service.AuthorizeAsync(h.Request("filesystem.read", InsidePath), TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);

        captured!.AllowedScopes.Should().BeEquivalentTo(
            new[] { PermissionGrantScope.Once, PermissionGrantScope.Session });
    }

    // === 批次 1.4：MCP server 粒度资源门（mcp.execute 归资源类，Allow 规则不短路） ===

    // build 默认 Allow(Tool,"*") 不得短路 MCP 资源门——须进入询问（旧行为零审批）。
    [Fact]
    public async Task AuthorizeAsync_McpExecute_RuleAllow_ShouldNotShortCircuit()
    {
        var h = new Harness();
        h.SetAgentPolicy("build", PermissionRuleEntry.Allow(PermissionKind.Tool, "*"));
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("mcp.execute", "serverA", agentName: "build"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        h.AssertAskedOnce();
    }

    // server 粒度记忆：批一次 server 后，同 server 任意工具（资源恒为 server 名）免审。
    [Fact]
    public async Task AuthorizeAsync_McpExecute_ServerGrant_ShouldShortCircuitForSameServer()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("mcp.execute", "serverA", PermissionGrantScope.Session, PermissionEffect.Allow));
        h.SetupAsk(PermissionEffect.Deny, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("mcp.execute", "serverA"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    // server 记忆不外溢到其它 server——隔离性回归。
    [Fact]
    public async Task AuthorizeAsync_McpExecute_ServerGrant_ShouldNotCoverOtherServer()
    {
        var h = new Harness();
        h.Store.Add("s1", new PermissionGrant("mcp.execute", "serverA", PermissionGrantScope.Session, PermissionEffect.Allow));
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("mcp.execute", "serverB"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Allow);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        h.AssertAskedOnce();
    }

    // I2 回归：mcp.execute 经 PermissionKindMapper 映射为 McpTool，
    // 使 plan/explore 的 Deny(McpTool,"*") 生效（此前落默认 Tool → 规则静默失配而被弱化）。
    [Fact]
    public async Task AuthorizeAsync_McpExecute_AgentDenyMcpTool_ShouldDeny()
    {
        var h = new Harness();
        h.SetAgentPolicy("plan", PermissionEffect.Deny,
            PermissionRuleEntry.Deny(PermissionKind.McpTool, "*", 100));
        h.SetupAsk(PermissionEffect.Allow, PermissionGrantScope.Once);

        var resolution = await h.Service.AuthorizeAsync(
            h.Request("mcp.execute", "serverA", agentName: "plan"), TestContext.Current.CancellationToken);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.Policy);
        h.AssertNoAsk();
    }

    // plan/explore 白名单兜底不变：MCP 工具名不在 AllowedTools → 能力门（tool.execute）直接拒绝。
    [Fact]
    public async Task EvaluateToolAsync_McpTool_NotInAllowedTools_ShouldDeny()
    {
        var h = new Harness();
        var context = new PermissionContext
        {
            AgentName = "plan",
            Policy = new AgentPermissionPolicy
            {
                AllowedTools = new[] { "read", "grep" },
                DefaultEffect = PermissionEffect.Ask
            }
        };

        var result = await h.Service.EvaluateToolAsync(
            "serverA_do_thing", null, context, TestContext.Current.CancellationToken);

        result.Effect.Should().Be(PermissionEffect.Deny);
        result.Reason.Should().Contain("allowed list");
    }

    // === 测试夹具 ===

    private sealed class Harness
    {
        public PermissionGrantStore Store { get; } = new();
        public Mock<IPermissionRequestManager> Manager { get; } = new();
        public Mock<IPermissionSurfaceRegistry> Presentation { get; } = new();
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
                .Returns((PermissionRequest r, CancellationToken _) => Task.FromResult(new RequestTicket(r.RequestId ?? "req", r.SessionId)));
            Manager.Setup(m => m.WaitAsync(It.IsAny<RequestTicket>(), It.IsAny<CancellationToken>()))
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

        public void AssertAskedTimes(int count) => Manager.Verify(
            m => m.BeginAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(count));
    }

    private static IOptionsMonitor<SeeingAgentOptions> OptionsMonitor(SeeingAgentOptions value)
    {
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(value);
        return options.Object;
    }
}
