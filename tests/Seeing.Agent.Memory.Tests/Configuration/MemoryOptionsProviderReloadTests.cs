using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Memory.Configuration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

/// <summary>
/// MemoryOptionsProvider 迁移回归测试：移除自订阅后，重载改由 <see cref="MemoryReloadHandler"/> 触发。
/// </summary>
public class MemoryOptionsProviderReloadTests
{
    private static (MemoryOptionsProvider provider, Mock<IConfigSectionStore> store) CreateProvider(MemoryOptions initial)
    {
        var store = new Mock<IConfigSectionStore>();
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName)).Returns(initial);
        return (new MemoryOptionsProvider(store.Object), store);
    }

    [Fact]
    public void ConfigChanged事件_不再自动重载()
    {
        // Arrange
        var (provider, store) = CreateProvider(new MemoryOptions { Enabled = false });
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName))
            .Returns(new MemoryOptions { Enabled = true });

        // Act: 存储变更事件不应再触发 Provider 自订阅重载
        store.Raise(x => x.ConfigChanged += null,
            new ConfigChangedEventArgs { ChangedSections = new[] { ConfigSectionMemoryOptionsStore.SectionName } });

        // Assert
        provider.CurrentValue.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task 通过ReloadHandler_应触发重载()
    {
        // Arrange
        var (provider, store) = CreateProvider(new MemoryOptions { Enabled = false });
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName))
            .Returns(new MemoryOptions { Enabled = true });
        var handler = new MemoryReloadHandler(provider);

        // Act: 由 Handler 触发重载
        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { ConfigSectionMemoryOptionsStore.SectionName } },
            CancellationToken.None);

        // Assert
        provider.CurrentValue.Enabled.Should().BeTrue();
    }
}
