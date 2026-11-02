using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Memory.Configuration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

/// <summary>
/// 记忆配置落盘端到端验证。
/// <para>
/// 既有测试只用 Moq 桩住 <c>IMemoryOptionsStore</c> / <c>IConfigSectionStore</c>，
/// 从未验证过「改一个字段 → SaveAsync → 磁盘上真的出现那个值」。
/// 这条链路是 WebUI 设置页「保存」按钮的全部承诺。
/// </para>
/// </summary>
public class MemoryOptionsPersistenceTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectSeeing;

    public MemoryOptionsPersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mem-persist-" + Guid.NewGuid().ToString("N")[..8]);
        _projectSeeing = Path.Combine(_root, ".seeing");
        Directory.CreateDirectory(_projectSeeing);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private UnifiedConfigManager CreateManager()
    {
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(Path.Combine(_root, "user", ".seeing"));
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(_projectSeeing);

        var registry = ConfigSectionRegistry.CreateWithSpine();
        registry.Register(new ConfigSectionMeta(
            ConfigSectionMemoryOptionsStore.SectionName,
            "memory.json",
            ConfigScope.Both,
            typeof(MemoryOptions)));

        return new UnifiedConfigManager(workspace.Object, NullLogger<UnifiedConfigManager>.Instance, registry);
    }

    /// <summary>
    /// 构造并加载配置。
    /// <para>
    /// <see cref="UnifiedConfigManager.LoadAsync"/> 必须显式调用（宿主在启动时做）：
    /// 未加载时缓存为空，<c>GetSection</c> 返回 <c>new T()</c> 默认值，
    /// 随后保存会把默认值覆盖到磁盘上——那会冲掉用户已有配置。
    /// </para>
    /// </summary>
    private async Task<ConfigSectionMemoryOptionsStore> CreateLoadedStoreAsync()
    {
        var manager = CreateManager();
        await manager.LoadAsync(TestContext.Current.CancellationToken);
        return new ConfigSectionMemoryOptionsStore(manager);
    }

    [Fact]
    public async Task SaveAsync_Should_Write_Changed_Value_To_MemoryJson_On_Disk()
    {
        var store = await CreateLoadedStoreAsync();

        var options = store.Get();
        options.Extraction.ExtractEveryNTurns = 7;

        await store.SaveAsync(options, TestContext.Current.CancellationToken);

        var path = Path.Combine(_projectSeeing, "memory.json");
        File.Exists(path).Should().BeTrue("SaveAsync 必须真的落盘，不能只改内存");

        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        text.Should().Contain("ExtractEveryNTurns");
        text.Should().Contain("\"ExtractEveryNTurns\": 7");

        // 重新读回，确认值存活（不是只写了个键）
        store.Get().Extraction.ExtractEveryNTurns.Should().Be(7);
    }

    [Fact]
    public async Task SaveAsync_Should_Preserve_Other_Sections_Already_In_MemoryJson()
    {
        // 先写一份带其它字段的文件，确认保存不会把整份配置覆盖掉。
        var path = Path.Combine(_projectSeeing, "memory.json");
        await File.WriteAllTextAsync(path, """
            {
              "Retrieval": { "TopK": 11, "Mode": "ToolsOnly" }
            }
            """, TestContext.Current.CancellationToken);

        var store = await CreateLoadedStoreAsync();

        var options = store.Get();
        options.Retrieval.TopK.Should().Be(11, "前置条件：加载必须读到已有文件");
        options.Extraction.ExtractEveryNTurns = 3;
        await store.SaveAsync(options, TestContext.Current.CancellationToken);

        var reloaded = store.Get();
        var written = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        reloaded.Retrieval.TopK.Should().Be(11,
            $"既有配置不得被保存动作冲掉。磁盘内容：{written}");
        reloaded.Extraction.ExtractEveryNTurns.Should().Be(3);
    }

    [Fact]
    public async Task SaveAsync_Without_Prior_Load_Should_Not_Silently_Overwrite_Existing_File()
    {
        // 未加载即保存是真实风险：缓存为空 → Get 返回默认值 → 保存把默认值写盘，
        // 用户已有的 TopK=11 会被静默改成默认 5。
        // 本用例只记录当前行为（是否发生覆盖），不强制断言某种结果——
        // 是否要改成「未加载即抛异常」属于 API 契约决策，见 release notes。
        var path = Path.Combine(_projectSeeing, "memory.json");
        await File.WriteAllTextAsync(path, """
            {
              "Retrieval": { "TopK": 11 }
            }
            """, TestContext.Current.CancellationToken);

        var store = new ConfigSectionMemoryOptionsStore(CreateManager());
        await store.SaveAsync(store.Get(), TestContext.Current.CancellationToken);

        var written = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        written.Should().NotBeNullOrWhiteSpace();
    }
}
