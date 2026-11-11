using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 配置桥接守门：<c>seeing.json</c> 里写的 <c>Browser</c> 值必须真的能被
/// <see cref="IOptionsMonitor{TOptions}"/> 读到。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有一条 DI 级测试</b>：<c>services.AddOptions&lt;BrowserOptions&gt;()</c> 只登记
/// options 基础设施；没有 <c>ConfigSectionOptionsMonitor</c> 桥接时 <c>CurrentValue</c> 永远是
/// <c>new BrowserOptions()</c> 的默认值。<see cref="BrowserOptionsTests"/> 全部在包内
/// <c>new BrowserOptions{...}</c> 上断言，抓不到这个缺陷——而它的后果是
/// <c>Headless</c>/<c>ExecutablePath</c>/<c>AllowPrivateHosts</c> 静默无效，
/// 尤其 <c>AllowPrivateHosts</c> 这类安全相关开关（读不到就等于「配了也没用」）。
/// </para>
/// </remarks>
public class BrowserOptionsBindingTests
{
    private static ServiceProvider BuildProvider(BrowserOptions configured)
    {
        var services = new ServiceCollection();

        // 模块要求服务集合里已有 IConfigSectionRegistry 实例（它按实例反射登记节）
        var registry = new FakeSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        var store = new Mock<IConfigSectionStore>();
        store.Setup(s => s.GetSection<BrowserOptions>(BrowserOptions.SectionName)).Returns(configured);
        services.AddSingleton(store.Object);

        var module = new BrowserModule();
        module.ConfigureServices(services);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Section_ShouldBeRegisteredAsUserOnly()
    {
        var services = new ServiceCollection();
        var registry = new FakeSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        new BrowserModule().ConfigureServices(services);

        // UserOnly 是安全边界：ExecutablePath 决定「启动哪个可执行文件」，
        // AllowPrivateHosts 决定能否访问内网。项目级配置随仓库分发、内容由第三方控制，
        // 若允许项目级覆盖这两键，git clone + browser_open 就是任意程序执行 / SSRF。
        registry.TryGet(BrowserOptions.SectionName, out var meta).Should().BeTrue();
        meta.Scope.Should().Be(ConfigScope.UserOnly);
    }

    [Fact]
    public void ConfiguredValues_ShouldBeReadThroughOptionsMonitor()
    {
        var configured = new BrowserOptions
        {
            Headless = false,
            ExecutablePath = @"C:\custom\chrome.exe",
            AllowPrivateHosts = true,
            IdleSeconds = 42,
            ViewportWidth = 1024,
            ViewportHeight = 768,
            DefaultTimeoutSeconds = 7
        };

        using var provider = BuildProvider(configured);
        var options = provider.GetRequiredService<IOptionsMonitor<BrowserOptions>>().CurrentValue;

        options.Headless.Should().BeFalse("Headless 读不到就等于永远有头/无头固定");
        options.ExecutablePath.Should().Be(@"C:\custom\chrome.exe");
        options.AllowPrivateHosts.Should().BeTrue("读不到会让内网永远被拒，配置形同虚设");
        options.IdleSeconds.Should().Be(42);
        options.EffectiveWidth.Should().Be(1024);
        options.EffectiveHeight.Should().Be(768);
        options.OperationTimeout.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void IOptions_ShouldResolveToSameInstanceAsMonitor()
    {
        using var provider = BuildProvider(new BrowserOptions { Headless = false });

        var monitor = provider.GetRequiredService<IOptionsMonitor<BrowserOptions>>();
        var options = provider.GetRequiredService<IOptions<BrowserOptions>>();

        options.Value.Should().BeSameAs(monitor.CurrentValue);
        options.Value.Headless.Should().BeFalse();
    }

    [Fact]
    public void AdmitUrl_ShouldHonourConfiguredAllowPrivateHosts()
    {
        // 「配置是否生效」的最终判据是行为：配了 AllowPrivateHosts 就必须能放行内网。
        using var provider = BuildProvider(new BrowserOptions { AllowPrivateHosts = true });

        var options = provider.GetRequiredService<IOptionsMonitor<BrowserOptions>>();
        var host = new BrowserSessionHost(
            new BrowserEngine(options),
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserSessionHost>.Instance);

        host.AdmitUrl("http://127.0.0.1:3000").Allowed.Should().BeTrue(
            "AllowPrivateHosts=true 从配置读到了，端点的准入判定才跟着变");
    }

    /// <summary>最小配置节注册表（只记录登记项）。</summary>
    private sealed class FakeSectionRegistry : IConfigSectionRegistry
    {
        private readonly Dictionary<string, ConfigSectionMeta> _sections = new(StringComparer.OrdinalIgnoreCase);

        public void Register(ConfigSectionMeta meta) => _sections[meta.Key] = meta;

        public IReadOnlyCollection<ConfigSectionMeta> Sections => _sections.Values;

        public bool TryGet(string key, out ConfigSectionMeta meta)
            => _sections.TryGetValue(key, out meta!);
    }
}
