using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Configuration;
using Xunit;

namespace Seeing.Gateway.Tests.Channels;

/// <summary>
/// 工作区切换后渠道路径必须跟着走。
/// <para><b>钉住的缺陷</b>：<see cref="ChannelHostConfigStore"/> 曾把
/// <c>{项目}/.seeing/gateway-clients</c> 缓存在构造函数里。它是单例，工作区切换后仍指向旧工作区，
/// 于是「按新根重启 ChannelHost」读的是旧工作区的配置文件——新工作区没有该文件时
/// <c>StartCoreAsync</c> 直接静默返回，表现为「切换工作区后渠道全部消失」；
/// 保存渠道配置也会写进旧工作区。</para>
/// </summary>
public class ChannelHostConfigStoreWorkspaceTests : IDisposable
{
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "gw-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "gw-b-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ClientsDirectory_AfterWorkspaceSwitch_FollowsCurrentWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_rootA, ".seeing"));
        Directory.CreateDirectory(Path.Combine(_rootB, ".seeing"));

        var workspace = new MutableWorkspace(Path.Combine(_rootA, ".seeing"));
        var store = CreateStore(workspace);

        store.ClientsDirectory.Should().Be(Path.Combine(_rootA, ".seeing", "gateway-clients"));

        workspace.ProjectSeeingDirectory = Path.Combine(_rootB, ".seeing");

        store.ClientsDirectory.Should().Be(Path.Combine(_rootB, ".seeing", "gateway-clients"),
            "单例不得把工作区路径钉死在构造时");
        store.GetRuntimeConfigPath("qq").Should()
            .Be(Path.Combine(_rootB, ".seeing", "gateway-clients", "qq.json"));
        store.GetRuntimeStatePath("qq").Should()
            .Be(Path.Combine(_rootB, ".seeing", "gateway-clients", "qq.state.json"));
    }

    [Fact]
    public async Task SaveAndLoadRuntimeState_AfterWorkspaceSwitch_UsesNewWorkspaceFile()
    {
        Directory.CreateDirectory(Path.Combine(_rootA, ".seeing"));
        Directory.CreateDirectory(Path.Combine(_rootB, ".seeing"));

        var workspace = new MutableWorkspace(Path.Combine(_rootA, ".seeing"));
        var store = CreateStore(workspace);

        await store.SaveRuntimeStateAsync("qq", new GatewayClientRuntimeState
        {
            ProcessId = 111,
            Status = GatewayClientStatuses.Running
        });

        workspace.ProjectSeeingDirectory = Path.Combine(_rootB, ".seeing");

        var state = await store.LoadRuntimeStateAsync("qq");
        state.ProcessId.Should().BeNull("新工作区还没有状态文件，不得读到旧工作区的进程号");

        await store.SaveRuntimeStateAsync("qq", new GatewayClientRuntimeState
        {
            ProcessId = 222,
            Status = GatewayClientStatuses.Running
        });

        File.Exists(Path.Combine(_rootB, ".seeing", "gateway-clients", "qq.state.json"))
            .Should().BeTrue("切换后的状态必须落到新工作区");
    }

    private static ChannelHostConfigStore CreateStore(IWorkspaceProvider workspace)
    {
        var sectionStore = new Mock<IConfigSectionStore>();
        sectionStore.Setup(x => x.GetSection<GatewayOptions>("Gateway")).Returns(new GatewayOptions());
        sectionStore.Setup(x => x.GetSection<GatewayClientsOptions>("GatewayClients"))
            .Returns(new GatewayClientsOptions { Channels = new(StringComparer.OrdinalIgnoreCase) });

        var registry = new GatewayChannelRegistry(
            NullLogger<GatewayChannelRegistry>.Instance, sectionStore.Object);

        return new ChannelHostConfigStore(sectionStore.Object, workspace, registry);
    }

    public void Dispose()
    {
        TryDelete(_rootA);
        TryDelete(_rootB);
        GC.SuppressFinalize(this);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>可变工作区：模拟切换时 <c>ProjectSeeingDirectory</c> 变化。</summary>
    private sealed class MutableWorkspace(string projectSeeingDirectory) : IWorkspaceProvider
    {
        public string UserSeeingDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

        public string ProjectSeeingDirectory { get; set; } = projectSeeingDirectory;

        public bool IsResolved => true;
        public string? ResolvedRoot => Path.GetDirectoryName(ProjectSeeingDirectory);
        public string StartupDirectory => ResolvedRoot!;
        public WorkspaceResolutionSource ResolutionSource => WorkspaceResolutionSource.ManualSwitch;
        public string? GlobalWorkspaceRoot => null;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged
        {
            add { }
            remove { }
        }

        public void SetInitialWorkspace(string workspaceRoot, WorkspaceResolutionSource source) =>
            ProjectSeeingDirectory = Path.Combine(workspaceRoot, ".seeing");

        public void SetWorkspaceRoot(string workspaceRoot) =>
            ProjectSeeingDirectory = Path.Combine(workspaceRoot, ".seeing");

        public string GetSeeingDirectory(ConfigLevel level) =>
            level == ConfigLevel.User ? UserSeeingDirectory : ProjectSeeingDirectory;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
