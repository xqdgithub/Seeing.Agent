using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Memory.Configuration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

public class MemoryReloadHandlerTests
{
    private static (MemoryOptionsProvider provider, Mock<IConfigSectionStore> store) CreateProvider(MemoryOptions initial)
    {
        var store = new Mock<IConfigSectionStore>();
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName)).Returns(initial);
        return (new MemoryOptionsProvider(store.Object), store);
    }

    [Fact]
    public async Task ReloadAsync_包含Memory节_应重载配置()
    {
        // Arrange
        var (provider, store) = CreateProvider(new MemoryOptions { Enabled = false });
        var handler = new MemoryReloadHandler(provider);
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName))
            .Returns(new MemoryOptions { Enabled = true });

        // Act
        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Memory" } }, CancellationToken.None);

        // Assert
        provider.CurrentValue.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ReloadAsync_空节列表_应全量重载()
    {
        // Arrange
        var (provider, store) = CreateProvider(new MemoryOptions { Enabled = false });
        var handler = new MemoryReloadHandler(provider);
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName))
            .Returns(new MemoryOptions { Enabled = true });

        // Act
        await handler.ReloadAsync(new ConfigChange { ChangedSections = Array.Empty<string>() }, CancellationToken.None);

        // Assert
        provider.CurrentValue.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ReloadAsync_其他配置节_不应重载()
    {
        // Arrange
        var (provider, store) = CreateProvider(new MemoryOptions { Enabled = false });
        var handler = new MemoryReloadHandler(provider);
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName))
            .Returns(new MemoryOptions { Enabled = true });

        // Act
        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Other" } }, CancellationToken.None);

        // Assert
        provider.CurrentValue.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ReloadAsync_非ConfigChange信号_应忽略()
    {
        // Arrange
        var (provider, store) = CreateProvider(new MemoryOptions { Enabled = false });
        var handler = new MemoryReloadHandler(provider);
        store.Setup(x => x.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName))
            .Returns(new MemoryOptions { Enabled = true });

        // Act
        await handler.ReloadAsync(new WorkspaceChange(), CancellationToken.None);

        // Assert
        provider.CurrentValue.Enabled.Should().BeFalse();
    }
}