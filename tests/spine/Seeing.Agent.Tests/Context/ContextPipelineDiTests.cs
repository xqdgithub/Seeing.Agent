using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Context.Sources;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 管道接线自检：Core DI 必须能解析装配器、指令管理器与内置指令源（单例）。
/// </summary>
public sealed class ContextPipelineDiTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"seeing-context-di-{Guid.NewGuid():N}");

    [Fact]
    public void AddSeeingCore_ShouldResolve_ContextPipeline()
    {
        Directory.CreateDirectory(_tempDir);
        var dir = _tempDir;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(provider => provider.ProjectSeeingDirectory == dir));
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        using var provider = services.BuildServiceProvider();

        var assembler = provider.GetRequiredService<IContextAssembler>();
        assembler.Should().BeSameAs(provider.GetRequiredService<IContextAssembler>());

        provider.GetRequiredService<IInstructionFileReader>().Should().NotBeNull();
        provider.GetServices<IContextSource>()
            .Should().ContainSingle(source => source is ProjectInstructionsContextSource);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
