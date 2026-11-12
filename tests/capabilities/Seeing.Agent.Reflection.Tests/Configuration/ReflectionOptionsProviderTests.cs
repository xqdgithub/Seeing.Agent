using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Extensions;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Configuration;

/// <summary>
/// 配置节的登记与读取路径。
/// <para>
/// 这条测试存在的理由是一个真实故障：文档写「配置存 seeing.json」，代码注册的却是
/// 独立文件 <c>reflection.json</c>，而 <c>UnifiedConfigManager</c> 只把
/// <c>FileName == "seeing.json"</c> 的节当嵌套节读。结果按文档配置的用户
/// <b>被完全忽略且没有任何告警</b>，功能看起来「开了却没反应」。
/// 因此这里把「文件名 / 作用域」两个会决定「读不读得到」的事实钉死。
/// </para>
/// </summary>
public sealed class ReflectionOptionsProviderTests
{
    [Fact]
    public void Meta_Should_Point_To_Dedicated_File_Not_Seeing_Json()
    {
        var meta = ReflectionOptionsProvider.Meta;

        meta.Key.Should().Be("Reflection");
        meta.FileName.Should().Be(ReflectionOptionsProvider.FileName);
        meta.FileName.Should().NotBe("seeing.json",
            "写成 seeing.json 会让 UnifiedConfigManager 按嵌套节读取，而本节的注册名不同 → 静默失效");
        meta.Scope.Should().Be(ConfigScope.Both, "用户级与项目级都应可配（运行时按字段深度合并）");
        meta.SectionType.Should().Be(typeof(ReflectionOptions));
    }

    [Fact]
    public void AddSeeingReflection_Should_Register_Section_On_Shared_Registry()
    {
        var registry = new ConfigSectionRegistryStub();
        var services = new ServiceCollection();

        services.AddSeeingReflection(registry);

        registry.TryGet("Reflection", out var meta).Should().BeTrue(
            "未登记的话配置节既不会出现在 WebUI，也不进统一校验");
        meta.FileName.Should().Be(ReflectionOptionsProvider.FileName);
    }

    [Fact]
    public void Provider_Should_Fall_Back_To_Defaults_Without_Store()
    {
        // 独立装配（单元测试 / 极简宿主）没有 IConfigSectionStore：退回默认值而不是抛异常。
        using var provider = new ReflectionOptionsProvider();

        var options = provider.CurrentValue;

        options.Enabled.Should().BeFalse("默认关闭：自动复盘会产生额外模型调用");
        options.Mode.Should().Be(ReflectionMode.SuggestOnly);
        options.TurnNudgeInterval.Should().Be(10);
        options.IdleMinutes.Should().Be(30);
    }

    [Fact]
    public void Provider_Should_Rebuild_On_Section_Change()
    {
        var store = new ConfigSectionStoreStub(new ReflectionOptions { Enabled = false });
        var provider = new ReflectionOptionsProvider(store);

        provider.CurrentValue.Enabled.Should().BeFalse();

        store.Value = new ReflectionOptions { Enabled = true, Mode = ReflectionMode.ApplyCreate };
        store.RaiseChange(new ConfigChangedEventArgs { ChangedSections = ["Reflection"] });

        provider.CurrentValue.Enabled.Should().BeTrue();
        provider.CurrentValue.Mode.Should().Be(ReflectionMode.ApplyCreate);
    }

    [Fact]
    public void Provider_Should_Ignore_Unrelated_Section_Change()
    {
        // 其它能力包改配置不应触发复盘选项重建（热重载路径上的噪声来源）。
        var store = new ConfigSectionStoreStub(new ReflectionOptions { Enabled = false });
        var provider = new ReflectionOptionsProvider(store);

        store.Value = new ReflectionOptions { Enabled = true };
        store.RaiseChange(new ConfigChangedEventArgs { ChangedSections = ["Memory"] });

        provider.CurrentValue.Enabled.Should().BeFalse("只关心本节");
    }

    private sealed class ConfigSectionRegistryStub : IConfigSectionRegistry
    {
        private readonly Dictionary<string, ConfigSectionMeta> _sections = new(StringComparer.OrdinalIgnoreCase);

        public void Register(ConfigSectionMeta meta) => _sections[meta.Key] = meta;

        public IReadOnlyCollection<ConfigSectionMeta> Sections => _sections.Values;

        public bool TryGet(string key, out ConfigSectionMeta meta) => _sections.TryGetValue(key, out meta!);
    }

    private sealed class ConfigSectionStoreStub(ReflectionOptions initial) : IConfigSectionStore
    {
        public ReflectionOptions Value { get; set; } = initial;

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

        public T GetSection<T>(string sectionName) where T : class, new()
            => Value as T ?? new T();

        public Task SaveSectionAsync<T>(
            string sectionName, T value, ConfigLevel level = ConfigLevel.Project, CancellationToken ct = default)
            where T : class
            => Task.CompletedTask;

        public void RaiseChange(ConfigChangedEventArgs args) => ConfigChanged?.Invoke(this, args);
    }
}
