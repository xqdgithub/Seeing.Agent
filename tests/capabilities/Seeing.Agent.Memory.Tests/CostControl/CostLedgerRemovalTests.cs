using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration.Tools;
using Xunit;

namespace Seeing.Agent.Memory.Tests.CostControl;

/// <summary>
/// 死账本删除守门测试。
/// <para>
/// Memory 包曾自带 <c>ITokenTracker</c>/<c>IQuotaManager</c>/<c>SqliteTokenTracker</c>/<c>DailyQuotaManager</c>
/// 与一个同名 <c>TokenUsage</c> record，与 <see cref="Seeing.Agent.Abstractions.Llm.TokenUsage"/> 重名并存且零生产消费者。
/// Token 用量的唯一权威来源是 <see cref="Seeing.Agent.Abstractions.Execution.ExecutionOutcome.Usage"/>。
/// </para>
/// <para>
/// 同批清理的还有两处配套残留：从未被生产代码调用的 <c>SchemaManager</c>（其 V1 迁移唯一地建了
/// <c>token_usage</c>/<c>quota_state</c> 死表），以及可写可落盘却永久无效的 <c>MemoryCostOptions</c>。
/// </para>
/// </summary>
public sealed class CostLedgerRemovalTests
{
    [Fact]
    public void Memory_Assembly_Should_Not_Define_TokenTracker()
    {
        var names = typeof(MemoryModule).Assembly.GetTypes().Select(t => t.Name).ToArray();

        names.Should().NotContain(new[]
        {
            "ITokenTracker", "IQuotaManager", "SqliteTokenTracker", "DailyQuotaManager",
        });
    }

    [Fact]
    public void Memory_Assembly_Should_Not_Define_SchemaManager()
    {
        var names = typeof(MemoryModule).Assembly.GetTypes().Select(t => t.Name).ToArray();

        names.Should().NotContain(new[] { "SchemaManager", "IMigration", "V1_InitialSchema" });
    }

    [Fact]
    public void MemoryOptions_Should_Not_Expose_Cost()
    {
        typeof(MemoryOptions).GetProperty("Cost").Should().BeNull(
            "MemoryCostOptions 从未被任何代码读取，但它会被序列化写进 memory.json，属用户可见的无效配置");
        typeof(MemoryOptions).Assembly.GetTypes().Select(t => t.Name)
            .Should().NotContain("MemoryCostOptions");
    }

    [Fact]
    public void Memory_Assembly_Should_Not_Define_Conflicting_TokenUsage() =>
        typeof(MemoryModule).Assembly.GetTypes()
            .Where(t => t.Name == "TokenUsage")
            .Should().BeEmpty("TokenUsage 应统一使用 Abstractions.Llm 的契约类型");

    [Fact]
    public void Memory_Assembly_Should_Not_Define_Conflicting_QuotaUsage() =>
        typeof(MemoryModule).Assembly.GetTypes()
            .Where(t => t.Name == "QuotaUsage")
            .Should().BeEmpty("配额概念随 IQuotaManager 一并移除");

    [Fact]
    public void AddMemoryServices_Should_Not_Register_CostLedger_Descriptors()
    {
        // Arrange
        var services = new ServiceCollection();
        AddMinimalTestDependencies(services);

        // Act
        services.AddMemoryServices(new StubConfigSectionRegistry(), "Data Source=:memory:");

        // Assert
        services.Where(d => d.ServiceType.Name is "ITokenTracker" or "IQuotaManager"
                                             or "SqliteTokenTracker" or "DailyQuotaManager")
            .Should().BeEmpty("死账本已删除，不应再登记任何 DI 描述符");
    }

    [Fact]
    public void Memory_Module_Should_Still_Resolve_After_Removal()
    {
        // Arrange：最小 DI 容器（日志 + 配置存储 + 文件存储替身，避免触碰用户目录）
        var services = new ServiceCollection();
        AddMinimalTestDependencies(services);
        services.AddMemoryServices(new StubConfigSectionRegistry(), "Data Source=:memory:");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        // Act
        using var scope = provider.CreateScope();
        var memoryService = scope.ServiceProvider.GetService<IMemoryService>();
        var recallService = provider.GetService<IMemoryRecallService>();
        var index = provider.GetService<IMemoryIndex>();
        var graph = provider.GetService<IMemoryGraph>();
        var rateLimiter = provider.GetService<IRateLimiter>();
        var searchTool = provider.GetService<MemorySearchTool>();

        // Assert：删除后核心服务链仍可解析
        memoryService.Should().NotBeNull();
        recallService.Should().NotBeNull();
        index.Should().NotBeNull();
        graph.Should().NotBeNull();
        rateLimiter.Should().NotBeNull("IRateLimiter 与死账本无关，须保留");
        searchTool.Should().NotBeNull();
    }

    private static void AddMinimalTestDependencies(IServiceCollection services)
    {
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfigSectionStore>(new StubConfigSectionStore());
        // 先登记替身：AddMemoryServices 使用 TryAdd，不会覆盖，避免写入用户主目录
        services.AddSingleton(Mock.Of<IFileStore>());
    }

    private sealed class StubConfigSectionStore : IConfigSectionStore
    {
        private readonly Dictionary<string, object> _sections = new(StringComparer.OrdinalIgnoreCase);
        public T GetSection<T>(string sectionName) where T : class, new() =>
            _sections.TryGetValue(sectionName, out var value) && value is T typed ? typed : new T();

        public Task SaveSectionAsync<T>(
            string sectionName,
            T value,
            ConfigLevel level = ConfigLevel.Project,
            CancellationToken ct = default) where T : class
        {
            _sections[sectionName] = value;
            return Task.CompletedTask;
        }

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
        {
            add { }
            remove { }
        }
    }
}
