using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Core.Ui;
using Seeing.Agent.Workbench.Tools;
using System.Text.Json;

namespace Seeing.Agent.Workbench.Tests;

/// <summary>
/// <c>list_workbenches</c> / <c>open_workbench</c> 的门闸与自解释失败文案。
/// 每道门都要有判别力：把它删掉时至少一条用例必须变红。
/// </summary>
public class WorkbenchToolsTests
{
    private static readonly UiPoint DockPoint = new()
    {
        Id = WorkbenchPoints.DockId,
        DisplayName = "会话工作台",
        PayloadKind = UiPayloadKind.Workbench,
        Scope = UiVisibilityScope.Session
    };

    private static WorkbenchContribution Files(bool contributesContext = true) => new()
    {
        Point = WorkbenchPoints.DockId,
        Id = "files",
        Title = "文件",
        ViewKey = WorkbenchPoints.ViewKeyFor("files"),
        Requires = ["files.workspace"],
        Order = 10,
        DefaultDock = WorkbenchDock.Right,
        OpenPayloadSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string" } },
            required = new[] { "path" }
        }),
        ContributesContext = contributesContext
    };

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    // ── list_workbenches ──

    [Fact]
    public async Task List_WithoutPointCatalog_ShouldReportZero()
    {
        var query = new Mock<IUiContributionQuery>();
        var tool = new ListWorkbenchesTool(NullLogger<ListWorkbenchesTool>.Instance, query.Object, points: null);

        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("可用工作台：0");
        query.Verify(
            q => q.GetVisible<WorkbenchContribution>(It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never,
            "没有扩展点声明就不该去查注册表");
    }

    [Fact]
    public async Task List_ShouldRenderIdTitleDockAndSchema()
    {
        var query = new Mock<IUiContributionQuery>();
        query.Setup(q => q.GetVisible<WorkbenchContribution>(WorkbenchPoints.DockId, It.IsAny<string?>()))
            .Returns([Files()]);

        var tool = new ListWorkbenchesTool(
            NullLogger<ListWorkbenchesTool>.Instance, query.Object, new PointCatalog(DockPoint));

        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("`files`").And.Contain("文件").And.Contain("右");
        result.Output.Should().Contain("path");
        result.Metadata!["count"].Should().Be(1);
    }

    [Fact]
    public async Task List_Empty_ShouldExplainWhy()
    {
        var query = new Mock<IUiContributionQuery>();
        query.Setup(q => q.GetVisible<WorkbenchContribution>(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns([]);

        var tool = new ListWorkbenchesTool(
            NullLogger<ListWorkbenchesTool>.Instance, query.Object, new PointCatalog(DockPoint));

        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext());

        result.Success.Should().BeTrue("空列表是事实，不是错误");
        result.Output.Should().Contain("可用工作台：0");
        result.Output.Should().Contain("模块");
    }

    // ── open_workbench ──

    [Fact]
    public async Task Open_WithoutPointCatalog_ShouldFail()
    {
        var query = new Mock<IUiContributionQuery>();
        var registry = new UiContributionRegistry();
        var tool = new OpenWorkbenchTool(
            NullLogger<OpenWorkbenchTool>.Instance, query.Object, registry, points: null);

        var result = await tool.ExecuteAsync(Args(new { workbenchId = "files" }), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未声明工作台扩展点");
    }

    [Fact]
    public async Task Open_UnknownId_ShouldListAvailable()
    {
        var (tool, _, _, _) = BuildOpen();

        var result = await tool.ExecuteAsync(Args(new { workbenchId = "nope" }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未知工作台").And.Contain("`files`");
    }

    [Fact]
    public async Task Open_RegistryReservedId_ShouldFail()
    {
        var (tool, _, _, _) = BuildOpen();

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = WorkbenchRequestKinds.ReservedRegistryId }), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("保留 id");
    }

    [Fact]
    public async Task Open_DeclaredButModuleDisabled_ShouldSayWhichModule()
    {
        var (tool, _, registry, _) = BuildOpen(visible: []);
        registry.AddRange("files.workspace", [Files()]);

        var result = await tool.ExecuteAsync(Args(new { workbenchId = "files" }), new ToolContext
        {
            SessionId = "s1",
            Services = BuildServices(catalog: new Catalog("memory"))
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未启用").And.Contain("files.workspace");
    }

    [Fact]
    public async Task Open_MissingRequiredPayload_ShouldFail()
    {
        var (tool, _, _, _) = BuildOpen();

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = "files", payload = new { } }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("path");
    }

    [Fact]
    public async Task Open_WrongPayloadType_ShouldFail()
    {
        var (tool, _, _, _) = BuildOpen();

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = "files", payload = new { path = 42 } }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("string");
    }

    [Fact]
    public async Task Open_InvalidDock_ShouldFail()
    {
        var (tool, _, _, _) = BuildOpen();

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = "files", dock = "middle", payload = new { path = "a.md" } }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("left 或 right");
    }

    [Fact]
    public async Task Open_NoSurface_ShouldFailInsteadOfPretending()
    {
        var (tool, _, _, surfaces) = BuildOpen();
        surfaces.Setup(s => s.CanSurface("s1")).Returns(false);

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = "files", payload = new { path = "a.md" } }),
            new ToolContext
            {
                SessionId = "s1",
                Services = BuildServices(includeSurface: true, surfaces: surfaces.Object)
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("没有可呈现该会话的界面");
    }

    [Fact]
    public async Task Open_NoRequestChannel_ShouldFail()
    {
        var (tool, _, _, _) = BuildOpen();

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = "files", payload = new { path = "a.md" } }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("宿主请求通道未接入");
    }

    [Fact]
    public async Task Open_Completed_ShouldReturnMetadata()
    {
        var (tool, manager, _, surfaces) = BuildOpen();
        surfaces.Setup(s => s.CanSurface("s1")).Returns(true);
        manager.Setup(m => m.BeginAsync(It.IsAny<HostRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestTicket("r1", "s1"));
        manager.Setup(m => m.WaitAsync(It.IsAny<RequestTicket>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResponse
            {
                RequestId = "r1",
                SessionId = "s1",
                Status = HostResponseStatus.Completed
            });

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = "files", dock = "left", payload = new { path = "a.md" } }),
            new ToolContext
            {
                SessionId = "s1",
                Services = BuildServices(
                    includeSurface: true, manager: manager.Object, surfaces: surfaces.Object)
            });

        result.Success.Should().BeTrue();
        result.Metadata!["workbench_id"].Should().Be("files");
        result.Metadata!["dock"].Should().Be("Left");
        result.Output.Should().Contain("左侧");

        manager.Verify(m => m.BeginAsync(
            It.Is<HostRequest>(r => r.Kind == WorkbenchRequestKinds.RegistryOpen),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Open_UnavailableResponse_ShouldFail()
    {
        var (tool, manager, _, surfaces) = BuildOpen();
        surfaces.Setup(s => s.CanSurface("s1")).Returns(true);
        manager.Setup(m => m.BeginAsync(It.IsAny<HostRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestTicket("r1", "s1"));
        manager.Setup(m => m.WaitAsync(It.IsAny<RequestTicket>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResponse { Status = HostResponseStatus.Unavailable });

        var result = await tool.ExecuteAsync(
            Args(new { workbenchId = "files", payload = new { path = "a.md" } }),
            new ToolContext
            {
                SessionId = "s1",
                Services = BuildServices(
                    includeSurface: true, manager: manager.Object, surfaces: surfaces.Object)
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("界面未就绪");
    }

    // ── helpers ──

    private static (OpenWorkbenchTool Tool, Mock<IHostRequestManager> Manager, UiContributionRegistry Registry,
        Mock<IHostSurfaceRegistry> Surfaces) BuildOpen(
        IReadOnlyList<WorkbenchContribution>? visible = null)
    {
        var registry = new UiContributionRegistry(new PointCatalog(DockPoint));
        var workbenches = visible ?? [Files()];

        var query = new Mock<IUiContributionQuery>();
        query.Setup(q => q.GetVisible<WorkbenchContribution>(WorkbenchPoints.DockId, It.IsAny<string?>()))
            .Returns(workbenches);

        var manager = new Mock<IHostRequestManager>();
        var surfaces = new Mock<IHostSurfaceRegistry>();

        var tool = new OpenWorkbenchTool(
            NullLogger<OpenWorkbenchTool>.Instance,
            query.Object,
            registry,
            new PointCatalog(DockPoint));

        return (tool, manager, registry, surfaces);
    }

    private static IServiceProvider BuildServices(
        IModuleCatalog? catalog = null,
        bool includeSurface = false,
        IHostRequestManager? manager = null,
        IHostSurfaceRegistry? surfaces = null)
        => new ServiceProviderStub(catalog, includeSurface, manager, surfaces);

    /// <summary>极简服务提供者：只回答工具会问的那几个类型。</summary>
    private sealed class ServiceProviderStub : IServiceProvider
    {
        private readonly IModuleCatalog? _catalog;
        private readonly bool _includeSurface;
        private readonly IHostRequestManager? _manager;
        private readonly IHostSurfaceRegistry? _surfaces;

        public ServiceProviderStub(
            IModuleCatalog? catalog,
            bool includeSurface,
            IHostRequestManager? manager,
            IHostSurfaceRegistry? surfaces)
        {
            _catalog = catalog;
            _includeSurface = includeSurface;
            _manager = manager;
            _surfaces = surfaces;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IModuleCatalog)) return _catalog;
            if (serviceType == typeof(IHostRequestManager)) return _manager;
            if (serviceType == typeof(IHostSurfaceRegistry)) return _includeSurface ? _surfaces : null;
            return null;
        }
    }

    private sealed class PointCatalog : IUiPointCatalog
    {
        private readonly Dictionary<string, UiPoint> _points;

        public PointCatalog(params UiPoint[] points)
            => _points = points.ToDictionary(p => p.Id, StringComparer.Ordinal);

        public IReadOnlyList<UiPoint> Points => _points.Values.ToArray();
        public UiPoint? Find(string pointId) => _points.GetValueOrDefault(pointId);
        public bool Supports(string pointId) => _points.ContainsKey(pointId);
    }

    private sealed class Catalog : IModuleCatalog
    {
        private readonly HashSet<string> _enabled;

        public Catalog(params string[] enabled)
            => _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<ModuleDescriptor> Available => [];
        public IReadOnlyCollection<string> Enabled => _enabled;
        public IReadOnlyDictionary<string, string?> Unhealthy => new Dictionary<string, string?>();
        public bool IsEnabled(string id) => _enabled.Contains(id);
        public bool IsAvailable(string id) => true;
    }
}
