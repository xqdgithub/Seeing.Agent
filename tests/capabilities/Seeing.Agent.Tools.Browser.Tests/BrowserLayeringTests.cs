using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 分层守门：浏览器能力包只能依赖 Abstractions（+ 原语 / support），**绝不能引用 Core**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须用反射钉住</b>：这条约束靠代码审查很容易漏——某次为了用 Core 里一个便捷类型
/// （<c>SeeingConfigService</c>、<c>UnifiedConfigManager</c>、或者只是个 <c>ConfigLevel</c> 枚举）
/// 加个 ProjectReference，编译立刻通过、测试全绿，但架构已经破了。
/// 后果是：能力包无法独立发布、无法在最小宿主里组合，也让依赖方向从「单向向下」退化成网状。
/// </para>
/// <para>
/// <b>为什么要逐类型检查而不是只看 csproj</b>：间接引用（A→B→Core）同样会带来类型泄漏，
/// 而 csproj 只看直接 ProjectReference 看不出来。检查本程序集引用的程序集列表才是真相。
/// </para>
/// </remarks>
public class BrowserLayeringTests
{
    /// <summary>能力包的程序集。</summary>
    private static readonly System.Reflection.Assembly CapabilityAssembly = typeof(BrowserOptions).Assembly;

    [Fact]
    public void CapabilityAssembly_ShouldNotReferenceCore()
    {
        var referenced = CapabilityAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        referenced.Should().NotContain(
            name => name.StartsWith("Seeing.Agent.Core", StringComparison.OrdinalIgnoreCase),
            "能力包不能引用核心脊柱（Core）；需要的契约应在 Abstractions 层");

        referenced.Should().NotContain(
            name => name.StartsWith("Seeing.Agent.Hosting", StringComparison.OrdinalIgnoreCase),
            "能力包不能引用宿主编排层");
    }

    [Theory]
    [InlineData("Seeing.Agent.Abstractions")]
    [InlineData("Seeing.Agent.Tools.Support")]
    [InlineData("Seeing.Files")]
    public void CapabilityAssembly_ShouldReferenceExpectedDownstreamLayers(string expected)
    {
        var referenced = CapabilityAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        // 注：PuppeteerSharp 等第三方包不在此列——这里只校验本项目内的分层。
        // Seeing.Files 是原语层（零依赖），能力包依赖它符合「只能向下」。
        referenced.Should().Contain(
            name => name.Equals(expected, StringComparison.OrdinalIgnoreCase),
            $"能力包应依赖 {expected}（原语层 / 契约层 / 支持层）");
    }

    [Fact]
    public void BrowserOptions_ShouldLiveInCapabilityNamespace()
    {
        // 配置类型必须留在能力包里（模块自己声明自己的配置节），
        // 搬到 Core 或 Hosting 会让「能力包自己声明配置」这条纪律失效。
        typeof(BrowserOptions).Namespace.Should().Be("Seeing.Agent.Tools.Browser");
    }

    [Fact]
    public void ResolveProfileDirectory_ShouldBePublicForHostDisplay()
    {
        // 设置页要展示「当前生效的 profile 路径」。宿主不能自己拼路径——
        // 那会让默认位置出现第二份实现，两边漂移时症状是「设置页显示与实际用的不同」。
        var method = typeof(BrowserEngine).GetMethod(
            "ResolveProfileDirectory",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull("宿主（设置页）需要读取解析结果来展示实际生效路径");
        method!.ReturnType.Should().Be<string>();
    }

    [Fact]
    public void BrowserOptions_DerivedProperties_ShouldNotBeSerialized()
    {
        // 设置页保存时序列化整个 BrowserOptions。派生属性（EffectiveWidth / IdleTimeout 等）
        // 没有 setter、读不回来，写进 seeing.json 只是噪声，还会让用户误以为可以手改。
        var derived = new[]
        {
            nameof(BrowserOptions.EffectiveWidth),
            nameof(BrowserOptions.EffectiveHeight),
            nameof(BrowserOptions.OperationTimeout),
            nameof(BrowserOptions.IdleTimeout)
        };

        var json = System.Text.Json.JsonSerializer.Serialize(new BrowserOptions());

        foreach (var name in derived)
        {
            json.Should().NotContain($"\"{name}\"",
                $"{name} 是派生只读属性，不应出现在序列化结果里");
        }
    }
}
