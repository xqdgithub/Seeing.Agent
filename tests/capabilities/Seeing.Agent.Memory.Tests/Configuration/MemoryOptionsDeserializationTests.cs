using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

/// <summary>
/// <see cref="MemoryOptions"/> 反序列化兼容性。
/// <para>
/// <c>MemoryCostOptions</c> 已删除，但存量 <c>memory.json</c> 里可能残留 <c>Cost</c> 键。
/// STJ 默认忽略未知成员，删除后加载**不得**失败，且其余字段照常生效。
/// </para>
/// <para>
/// <b>必须走生产路径</b>：前两版用例自带 <c>new JsonSerializerOptions{...}</c>，绕开了
/// <c>UnifiedConfigManager.JsonOptions</c>，因此生产侧一旦加上 <c>UnmappedMemberHandling.Disallow</c>
/// 用例仍全绿（假绿）。此处改为真实 <see cref="UnifiedConfigManager.LoadAsync"/> +
/// <see cref="UnifiedConfigManager.GetSection{T}"/>。
/// </para>
/// </summary>
public sealed class MemoryOptionsDeserializationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _userSeeing;
    private readonly string _projectSeeing;

    public MemoryOptionsDeserializationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mem-cost-compat-" + Guid.NewGuid().ToString("N"));
        _userSeeing = Path.Combine(_tempDir, "user", ".seeing");
        _projectSeeing = Path.Combine(_tempDir, "project", ".seeing");
        Directory.CreateDirectory(_userSeeing);
        Directory.CreateDirectory(_projectSeeing);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>构造与 <c>AddMemoryServices</c> 完全一致的注册表 + 真实 UnifiedConfigManager。</summary>
    private async Task<UnifiedConfigManager> CreateManagerAsync()
    {
        var registry = new StubConfigSectionRegistry();
        registry.Register(new ConfigSectionMeta(
            ConfigSectionMemoryOptionsStore.SectionName,
            "memory.json",
            ConfigScope.Both,
            typeof(MemoryOptions)));

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(_userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(_projectSeeing);

        var manager = new UnifiedConfigManager(
            workspace.Object, NullLogger<UnifiedConfigManager>.Instance, registry);
        await manager.LoadAsync(TestContext.Current.CancellationToken);
        return manager;
    }

    [Fact]
    public async Task GetSection_WithLegacyCostKey_ShouldNotThrow_AndIgnoreIt()
    {
        // Arrange：模拟存量 memory.json（含已删除的 Cost 节）
        const string legacyJson = """
        {
          "Capture": { "AutoCapture": false, "MaxSnippetChars": 2048 },
          "Extraction": { "ExtractEveryNTurns": 7 },
          "Cost": { "DailyTokenQuota": 500000, "RateLimitPerMinute": 30 }
        }
        """;
        await File.WriteAllTextAsync(
            Path.Combine(_projectSeeing, "memory.json"), legacyJson,
            TestContext.Current.CancellationToken);

        var manager = await CreateManagerAsync();

        // Act：走生产 GetSection（内部 node.Deserialize<T>(JsonOptions)，无 try/catch）
        var options = manager.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName);

        // Assert：不抛异常，已知字段生效，Cost 被静默忽略
        options.Should().NotBeNull();
        options.Capture.AutoCapture.Should().BeFalse();
        options.Capture.MaxSnippetChars.Should().Be(2048);
        options.Extraction.ExtractEveryNTurns.Should().Be(7);
        typeof(MemoryOptions).GetProperty("Cost").Should().BeNull(
            "MemoryCostOptions 已删除：该配置项可写可落盘却永久无效，属用户可见的谎言");
    }

    /// <summary>
    /// STJ 默认语义对照：未知成员默认被忽略，但显式 <c>UnmappedMemberHandling.Disallow</c> 下会被拒绝。
    /// <para>
    /// <b>本用例不守护生产配置</b>——它只自造两个 <see cref="JsonSerializerOptions"/> 做默认行为对照，
    /// 读不到 <c>UnifiedConfigManager.JsonOptions</c>（那是 <c>private static</c> 字段）。
    /// 实测也证实这一点：给生产侧加 <c>Disallow</c> 时本用例**仍然绿**。
    /// 真正守护生产路径的是 <see cref="GetSection_WithLegacyCostKey_ShouldNotThrow_AndIgnoreIt"/>。
    /// </para>
    /// </summary>
    [Fact]
    public void StjDefaults_Should_Tolerate_UnknownMembers()
    {
        const string legacyJson = """{ "Cost": { "DailyTokenQuota": 1 } }""";

        // 严格模式对照：确认这段 JSON 在 Disallow 下确实会炸
        var strict = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        var strictAct = () => JsonSerializer.Deserialize<MemoryOptions>(legacyJson, strict);
        strictAct.Should().Throw<JsonException>(
            "严格模式下未知的 Cost 键会被拒绝");

        // STJ 默认（未设置 UnmappedMemberHandling）应容忍未知成员
        var tolerant = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var tolerantAct = () => JsonSerializer.Deserialize<MemoryOptions>(legacyJson, tolerant);
        tolerantAct.Should().NotThrow();
    }

    [Fact]
    public void MemoryOptions_ShouldNotDefine_MemoryCostOptionsType()
    {
        var names = typeof(MemoryOptions).Assembly.GetTypes().Select(t => t.Name).ToArray();

        names.Should().NotContain("MemoryCostOptions");
    }

    [Fact]
    public void AddMemoryServices_Should_Register_Memory_Section_On_MemoryJson()
    {
        // 钉死配置落盘位置：memory.json（不是 seeing.json 的 Memory 节）。
        // 用户按文档改文件时依赖此事实。
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var registry = new StubConfigSectionRegistry();

        services.AddMemoryServices(registry, "Data Source=:memory:");

        registry.TryGet(ConfigSectionMemoryOptionsStore.SectionName, out var meta).Should().BeTrue();
        meta.FileName.Should().Be("memory.json");
        meta.Key.Should().Be("Memory");
        meta.SectionType.Should().Be(typeof(MemoryOptions));
    }
}
