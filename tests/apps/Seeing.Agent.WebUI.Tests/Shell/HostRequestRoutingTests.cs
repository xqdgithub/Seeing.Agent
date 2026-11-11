using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Workbench;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Shell;

/// <summary>
/// 宿主请求路由守门：工作台 kind 必须能被 <c>WorkbenchRequestHandlerRegistry</c> 解析到。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是「调用 browser_open / open_workbench 打不开工作台」的根因守门。</b>
/// <c>HostRequestDispatcher</c> 曾只查旧的 <c>HostRequestActivePanelRegistry</c>
/// （每会话单 handler，服务编辑器 <c>editor.*</c>），
/// 而工作台注册在按 kind 前缀路由的 <c>WorkbenchRequestHandlerRegistry</c> 里。
/// 于是 <c>workbench.registry.open</c> 永远找不到 handler，被判 <c>Unavailable</c>：
/// agent 侧看到「界面未就绪」，而界面其实是好的。
/// </para>
/// <para>
/// 这里钉住「注册表能按 kind 解析」这半边；dispatcher 是否两张表都查由代码约束
/// （它同时依赖两个 registry，缺一不可编译）。
/// </para>
/// </remarks>
public class HostRequestRoutingTests
{
    private static HostRequest Request(string sessionId, string kind) => new()
    {
        SessionId = sessionId,
        Kind = kind,
        Payload = null
    };

    [Fact]
    public void WorkbenchRegistryKind_ShouldResolveToRegisteredHandler()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        var handled = false;

        registry.Register(
            "s1",
            "circuit1",
            WorkbenchRequestKinds.RegistryPrefix,
            new WorkbenchRequestHandler((req, _) =>
            {
                handled = true;
                return Task.FromResult(new HostResponse
                {
                    RequestId = req.RequestId,
                    SessionId = req.SessionId,
                    Status = HostResponseStatus.Completed
                });
            }));

        var resolved = registry.TryResolve(
            "s1", "circuit1", Request("s1", WorkbenchRequestKinds.RegistryOpen),
            out var handler, out var ambiguous);

        resolved.Should().BeTrue(
            "workbench.registry.open 必须能解析——否则 open_workbench / browser_open 永远打不开工作台");
        ambiguous.Should().BeFalse();
        handler.Handler.Should().NotBeNull();

        // 顺带确认 handler 真的能跑通（不是只有一条空登记）
        handler.Handler(Request("s1", WorkbenchRequestKinds.RegistryOpen), CancellationToken.None)
            .GetAwaiter().GetResult();
        handled.Should().BeTrue();
    }

    [Fact]
    public void WorkbenchOwnAction_ShouldResolveByPrefix()
    {
        var registry = new WorkbenchRequestHandlerRegistry();

        registry.Register("s1", "c1", WorkbenchRequestKinds.For("browser", "seek"),
            new WorkbenchRequestHandler((req, _) => Task.FromResult(new HostResponse
            {
                RequestId = req.RequestId,
                SessionId = req.SessionId,
                Status = HostResponseStatus.Completed
            }), "Right"));

        registry.TryResolve("s1", "c1", Request("s1", "workbench.browser.seek"),
            out _, out _).Should().BeTrue();
    }

    [Fact]
    public void UnknownScope_ShouldNotResolve()
    {
        var registry = new WorkbenchRequestHandlerRegistry();
        registry.Register("s1", "c1", WorkbenchRequestKinds.RegistryPrefix,
            new WorkbenchRequestHandler((_, _) => Task.FromResult(new HostResponse
            {
                SessionId = "s1",
                Status = HostResponseStatus.Completed
            })));

        registry.TryResolve("s1", "c1", Request("s1", "editor.command"),
            out _, out _).Should().BeFalse("editor.* 不属于工作台作用域");
    }

    [Fact]
    public void HasAny_ShouldReflectRegistration()
    {
        var registry = new WorkbenchRequestHandlerRegistry();

        registry.HasAny("s1").Should().BeFalse();
        registry.Register("s1", "c1", WorkbenchRequestKinds.RegistryPrefix,
            new WorkbenchRequestHandler((_, _) => Task.FromResult(new HostResponse
            {
                SessionId = "s1",
                Status = HostResponseStatus.Completed
            })));
        registry.HasAny("s1").Should().BeTrue();
        registry.HasAny("s2").Should().BeFalse();
    }
}
