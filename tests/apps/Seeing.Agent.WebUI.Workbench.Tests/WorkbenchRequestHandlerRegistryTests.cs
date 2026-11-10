using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench.Tests;

/// <summary>
/// 宿主请求处理器注册表：最长 kind 前缀、跨 circuit 隔离、多实例按 dock 消歧。
/// <para>
/// 这是把「单面板」扩成「双 dock 多实例」后必须钉死的部分 —— 旧形状是
/// <c>(session, circuit) → 单 handler</c>，两个工作台并存时必然互相顶掉。
/// </para>
/// </summary>
public class WorkbenchRequestHandlerRegistryTests
{
    private const string Session = "s1";
    private const string Circuit = "c1";

    private static HostRequest Request(string kind, string? dock = null)
        => new()
        {
            RequestId = "r1",
            SessionId = Session,
            Kind = kind,
            Payload = dock is null
                ? null
                : JsonSerializer.SerializeToElement(new { dock })
        };

    private static WorkbenchRequestHandler Handler(string reply, string? dock = null)
        => new((req, _) => Task.FromResult(new HostResponse
        {
            RequestId = req.RequestId,
            SessionId = req.SessionId,
            Status = HostResponseStatus.Completed,
            Reason = reply
        }), dock);

    [Fact]
    public void TryResolve_NoRegistration_ShouldFail()
    {
        var registry = new WorkbenchRequestHandlerRegistry();

        registry.TryResolve(Session, Circuit, Request("workbench.files.command"), out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryResolve_LongestPrefixWins()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench", Handler("root"));
        registry.Register(Session, Circuit, "workbench.files", Handler("files"));

        registry.TryResolve(Session, Circuit, Request("workbench.files.command"), out var handler, out var ambiguous)
            .Should().BeTrue();

        ambiguous.Should().BeFalse();
        handler.Handler.Should().NotBeNull();
    }

    [Fact]
    public void TryResolve_EmptyScope_IsFallbackWithLowestPriority()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "", Handler("fallback"));
        registry.Register(Session, Circuit, "workbench.registry", Handler("registry"));

        registry.TryResolve(Session, Circuit, Request(WorkbenchRequestKinds.RegistryOpen), out _, out _)
            .Should().BeTrue();

        // 空 scope 只在不命中任何具体前缀时生效
        registry.TryResolve(Session, Circuit, Request("editor.command"), out _, out var ambiguous)
            .Should().BeTrue();
        ambiguous.Should().BeFalse();
    }

    [Fact]
    public void TryResolve_OtherCircuit_ShouldNotSeeHandler()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("files"));

        registry.TryResolve(Session, "c2", Request("workbench.files.command"), out _, out _)
            .Should().BeFalse("不同标签页互不干扰");
    }

    [Fact]
    public void TryResolve_OtherSession_ShouldNotSeeHandler()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("files"));

        registry.TryResolve("s2", Circuit, Request("workbench.files.command"), out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryResolve_TwoDocksWithDockInPayload_ShouldPickMatchingDock()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("left", "Left"));
        registry.Register(Session, Circuit, "workbench.files", Handler("right", "Right"));

        registry.TryResolve(Session, Circuit, Request("workbench.files.command", dock: "Right"), out var handler, out var ambiguous)
            .Should().BeTrue();

        ambiguous.Should().BeFalse();
        handler.Dock.Should().Be("Right");
    }

    [Fact]
    public void TryResolve_TwoDocksWithoutDock_ShouldReportAmbiguous()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("left", "Left"));
        registry.Register(Session, Circuit, "workbench.files", Handler("right", "Right"));

        registry.TryResolve(Session, Circuit, Request("workbench.files.command"), out var handler, out var ambiguous)
            .Should().BeTrue("registry 不做业务判定，只把歧义标出来");

        ambiguous.Should().BeTrue();
        handler.Handler.Should().NotBeNull();
    }

    [Fact]
    public void Register_SameDock_ShouldReplace()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("first", "Right"));
        registry.Register(Session, Circuit, "workbench.files", Handler("second", "Right"));

        registry.TryResolve(Session, Circuit, Request("workbench.files.command"), out var handler, out var ambiguous)
            .Should().BeTrue();

        ambiguous.Should().BeFalse("同 dock 重复登记是重渲染后的重新登记");
    }

    [Fact]
    public void Unregister_ShouldRemoveScope()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("files"));

        registry.Unregister(Session, Circuit, "workbench.files");

        registry.TryResolve(Session, Circuit, Request("workbench.files.command"), out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void UnregisterAll_ShouldClearEveryScopeForCircuit()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("files"));
        registry.Register(Session, Circuit, "workbench.registry", Handler("registry"));
        registry.Register(Session, "c2", "workbench.files", Handler("other"));

        registry.UnregisterAll(Session, Circuit);

        registry.HasAny(Session).Should().BeTrue("另一个 circuit 的登记仍在");
        registry.TryResolve(Session, Circuit, Request(WorkbenchRequestKinds.RegistryOpen), out _, out _)
            .Should().BeFalse();
        registry.TryResolve(Session, "c2", Request("workbench.files.command"), out _, out _)
            .Should().BeTrue();
    }

    [Fact]
    public void HasAny_ShouldIgnoreCircuitBoundary()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.HasAny(Session).Should().BeFalse();

        registry.Register(Session, Circuit, "workbench.files", Handler("files"));

        registry.HasAny(Session).Should().BeTrue();
        registry.HasAny("s2").Should().BeFalse();
    }

    [Fact]
    public void Register_BlankKeys_ShouldBeIgnored()
    {
        var registry = new WorkbenchRequestHandlerRegistry();

        registry.Register("", Circuit, "workbench.files", Handler("x"));
        registry.Register(Session, "", "workbench.files", Handler("x"));

        registry.HasAny(Session).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_EmptyKind_ShouldFail()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "", Handler("fallback"));

        registry.TryResolve(Session, Circuit, Request(""), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_ScopeBoundary_ShouldNotMatchSimilarPrefix()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.file", Handler("file"));

        // 'workbench.files.x' 不是 'workbench.file' 的子路径（前缀后必须紧跟 '.'）
        registry.TryResolve(Session, Circuit, Request("workbench.files.x"), out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryResolve_ExactScopeWithoutAction_ShouldMatch()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register(Session, Circuit, "workbench.files", Handler("files"));

        registry.TryResolve(Session, Circuit, Request("workbench.files"), out _, out _)
            .Should().BeTrue();
    }
}
