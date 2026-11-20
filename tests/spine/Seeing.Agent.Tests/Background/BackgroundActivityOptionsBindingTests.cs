using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Background;

/// <summary>
/// <c>BackgroundActivities</c> 配置节的桥接守门：写在 <c>seeing.json</c> 里的治理参数
/// 必须真的能被 <see cref="IOptionsMonitor{TOptions}"/> 读到。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有一条 DI 级测试</b>：<c>services.AddOptions&lt;BackgroundActivityOptions&gt;()</c>
/// 只登记 options 基础设施；没有 <c>ConfigSectionOptionsMonitor</c> 桥接时
/// <c>CurrentValue</c> 永远是 <c>new BackgroundActivityOptions()</c> 的默认值——
/// 而它的后果是 <c>MaxConcurrentPerSession</c> / 超时 / 输出上限全部静默无效，
/// 且没有任何报错。
/// </para>
/// </remarks>
public class BackgroundActivityOptionsBindingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bg-options-" + Guid.NewGuid().ToString("N"));

    private readonly string _userSeeing;
    private readonly string _projectSeeing;

    public BackgroundActivityOptionsBindingTests()
    {
        _userSeeing = Path.Combine(_root, "user", ".seeing");
        _projectSeeing = Path.Combine(_root, "project", ".seeing");
        Directory.CreateDirectory(_userSeeing);
        Directory.CreateDirectory(_projectSeeing);
    }

    [Fact]
    public async Task ConfiguredValues_ShouldBeReadThroughOptionsMonitor()
    {
        // Arrange：项目级 seeing.json 写 BackgroundActivities（嵌套在 SeeingAgent 下）
        await File.WriteAllTextAsync(
            Path.Combine(_projectSeeing, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "BackgroundActivities": { "MaxConcurrentPerSession": 2 }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        await using var provider = BuildProvider();
        await provider.GetRequiredService<UnifiedConfigManager>()
            .LoadAsync(TestContext.Current.CancellationToken);

        // Act
        var options = provider.GetRequiredService<IOptionsMonitor<BackgroundActivityOptions>>()
            .CurrentValue;

        // Assert：读不到就等于配置形同虚设，且只会在运行到上限时才异常
        options.MaxConcurrentPerSession.Should().Be(2);
    }

    [Fact]
    public void Section_ShouldBeRegisteredOnSharedRegistry()
    {
        using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IConfigSectionRegistry>();

        registry.TryGet(BackgroundActivityOptions.SectionName, out var meta).Should().BeTrue(
            "未登记到 registry 的节不会被 UnifiedConfigManager 收集，热重载与首屏读值都会落回默认值");

        meta.Scope.Should().Be(ConfigScope.Both);
        meta.SectionType.Should().Be(typeof(BackgroundActivityOptions));
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.UserSeeingDirectory).Returns(_userSeeing);
        workspace.SetupGet(w => w.ProjectSeeingDirectory).Returns(_projectSeeing);
        services.AddSingleton(workspace.Object);

        services.AddSingleton<ILoggerFactory>(
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));

        services.AddSeeingCore(registry);

        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 忽略清理时的文件锁竞争
        }
    }
}
