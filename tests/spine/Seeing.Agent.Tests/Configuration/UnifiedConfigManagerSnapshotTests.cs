using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// UnifiedConfigManager 快照原子替换语义（批次 4 F）：
/// 更新以新实例替换 SeeingAgent；LoadAsync 加载期间不暴露空缓存窗口。
/// </summary>
public class UnifiedConfigManagerSnapshotTests
{
    [Fact]
    public void SetSectionInMemory_ShouldReplaceSeeingAgentSnapshot()
    {
        var (manager, tempDir) = CreateManager(new ProbeRegistry(ConfigSectionRegistry.CreateWithSpine()));
        try
        {
            var before = manager.SeeingAgent;

            manager.SetSectionInMemory("DefaultModel", "gpt-x");

            manager.SeeingAgent.Should().NotBeSameAs(before);
            manager.SeeingAgent.DefaultModel.Should().Be("gpt-x");
            // 旧快照不被原地修改，保证并发读者不会观察到半更新对象
            before.DefaultModel.Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldNotExposeEmptyCacheWindow()
    {
        var probe = new ProbeRegistry(ConfigSectionRegistry.CreateWithSpine());
        var (manager, tempDir) = CreateManager(probe);
        try
        {
            // 先写入并缓存一个值
            await manager.SaveSectionAsync(
                "ToolOutput",
                new ToolOutputOptions { MaxInlineBytes = 12345 },
                ConfigLevel.Project,
                TestContext.Current.CancellationToken);

            ToolOutputOptions? observedDuringLoad = null;
            probe.OnSectionsAccess = () =>
            {
                if (observedDuringLoad != null)
                    return;
                observedDuringLoad = manager.GetSection<ToolOutputOptions>("ToolOutput");
            };

            await manager.LoadAsync(TestContext.Current.CancellationToken);

            observedDuringLoad.Should().NotBeNull("LoadAsync 加载期间应访问过配置节注册表");
            observedDuringLoad!.MaxInlineBytes.Should().Be(12345,
                "加载期间应保留旧快照，不得清空缓存暴露空窗口");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static (UnifiedConfigManager Manager, string TempDir) CreateManager(IConfigSectionRegistry registry)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ucm-snapshot-" + Guid.NewGuid().ToString("N"));
        var userSeeing = Path.Combine(tempDir, ".seeing");
        Directory.CreateDirectory(userSeeing);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(userSeeing);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            registry);
        return (manager, tempDir);
    }

    /// <summary>在每次访问 <see cref="Sections"/> 时触发回调，用于观测加载期间的缓存状态。</summary>
    private sealed class ProbeRegistry : IConfigSectionRegistry
    {
        private readonly ConfigSectionRegistry _inner;

        public ProbeRegistry(ConfigSectionRegistry inner) => _inner = inner;

        public Action? OnSectionsAccess { get; set; }

        public void Register(ConfigSectionMeta meta) => _inner.Register(meta);

        public IReadOnlyCollection<ConfigSectionMeta> Sections
        {
            get
            {
                OnSectionsAccess?.Invoke();
                return _inner.Sections;
            }
        }

        public bool TryGet(string key, out ConfigSectionMeta meta) => _inner.TryGet(key, out meta);
    }
}
