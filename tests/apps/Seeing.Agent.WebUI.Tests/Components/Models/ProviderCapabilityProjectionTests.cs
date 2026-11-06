using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.WebUI.Components.Models;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components.Models;

/// <summary>
/// Provider 能力投影的守门测试。
/// <para><b>背景</b>：模型页的能力多选框只表达「配置覆盖」，未勾选时它是空的。
/// 用户（包括实际反馈者）会把空框读成「这个 Provider 没有声明任何能力」，
/// 而扩展 Provider 实际上可能正在供出 chat + embedding。投影必须把这两件事分开说。</para>
/// </summary>
public class ProviderCapabilityProjectionTests
{
    [Fact]
    public void Build_WhenConfigHasNoCapabilities_ShouldReportDefaultButKeepEffective()
    {
        // SiliconFlow 的真实形状：配置只有 apiKey，作者默认值是 chat + embedding
        var info = Info(LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding);

        var view = ProviderCapabilityProjection.Build(info, null);

        view.Source.Should().Be(ProviderCapabilitySource.Default);
        view.Effective.Should().Be(LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding);
        view.DeclaredKeys.Should().BeEmpty("未配置覆盖时多选框本就该是空的");
        view.EffectiveText.Should().Contain("chat").And.Contain("embedding");
        view.SourceText(ProviderTypes.OpenAi, ProviderOrigin.Extension)
            .Should().Be("来源：插件默认",
                "扩展 Provider 的默认值来自插件作者，不是协议缺省");
    }

    [Fact]
    public void Build_WhenConfiguredProviderHasNoOverride_ShouldSayProtocolDefault()
    {
        var view = ProviderCapabilityProjection.Build(Info(LlmCapabilityKind.Chat), null);

        view.Source.Should().Be(ProviderCapabilitySource.Default);
        view.SourceText(ProviderTypes.OpenAi, ProviderOrigin.Configured)
            .Should().Be("来源：协议缺省（openai）");
    }

    [Fact]
    public void Build_WhenCapabilitiesDeclared_ShouldMarkDeclared()
    {
        var info = Info(LlmCapabilityKind.Chat);

        var view = ProviderCapabilityProjection.Build(info, new List<string> { "chat" });

        view.Source.Should().Be(ProviderCapabilitySource.Declared);
        view.DeclaredMask.Should().Be(LlmCapabilityKind.Chat);
        view.HasDrift.Should().BeFalse();
        view.SourceText(ProviderTypes.OpenAi, ProviderOrigin.Extension).Should().Be("来源：配置覆盖");
    }

    [Fact]
    public void Build_WhenDeclaredAsEmptyArray_ShouldReportDisabledNotNull()
    {
        // 显式空数组 = 明确关掉全部能力，与「没写」不是一回事
        var view = ProviderCapabilityProjection.Build(
            Info(LlmCapabilityKind.None), new List<string>());

        view.Source.Should().Be(ProviderCapabilitySource.Disabled);
        view.EffectiveText.Should().Be("（无）");
        view.SourceText(ProviderTypes.OpenAi, ProviderOrigin.Extension)
            .Should().Be("来源：配置已关闭全部能力");
    }

    [Fact]
    public void Build_WhenConfigHasUnknownKey_ShouldKeepItVisible()
    {
        // 未登记的名字不能被悄悄吞掉：用户要能看见自己写错了什么
        var view = ProviderCapabilityProjection.Build(
            Info(LlmCapabilityKind.Chat), new List<string> { "chat", "not-a-capability" });

        view.UnknownKeys.Should().Equal("not-a-capability");
        view.DeclaredMask.Should().Be(LlmCapabilityKind.Chat);
        view.HasDrift.Should().BeTrue();
        view.DriftText.Should().Contain("not-a-capability");
    }

    [Fact]
    public void Build_WhenEffectiveDiffersFromDeclared_ShouldReportDrift()
    {
        // Provider 主动裁掉了未实现的组合（例如端点不提供 embedding）
        var view = ProviderCapabilityProjection.Build(
            Info(LlmCapabilityKind.Chat), new List<string> { "chat", "embedding" });

        view.HasDrift.Should().BeTrue();
        view.DriftText.Should().Contain("embedding");
    }

    [Fact]
    public void Build_ShouldReadJsonArray_AsProducedByConfigLoad()
    {
        // LoadConfigAsync 从 JSON 读回时，List<string> 会以 JsonElement 形式到达 UI
        using var doc = JsonDocument.Parse("""["chat","embedding"]""");

        var view = ProviderCapabilityProjection.Build(
            Info(LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding),
            doc.RootElement);

        view.Source.Should().Be(ProviderCapabilitySource.Declared);
        view.DeclaredKeys.Should().Equal("chat", "embedding");
    }

    [Fact]
    public void Build_ShouldNormalizeCaseAndOrder_ButNotDropUnknowns()
    {
        var view = ProviderCapabilityProjection.Build(
            Info(LlmCapabilityKind.Chat),
            new List<string> { "Chat", " ", "CHAT", "weird" });

        view.DeclaredKeys.Should().Equal("chat", "weird");
        view.UnknownKeys.Should().Equal("weird");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    public void IsDeclared_ShouldDistinguishMissingFromEmpty(string? json, bool expected)
    {
        object? raw = json is null ? null : JsonDocument.Parse($"\"{json}\"").RootElement;

        ProviderCapabilityProjection.IsDeclared(raw).Should().Be(expected);
    }

    private static ProviderInfo Info(LlmCapabilityKind capabilities) => new()
    {
        Id = "siliconflow",
        Name = "SiliconFlow",
        Protocol = ProviderTypes.OpenAi,
        Capabilities = capabilities,
        Origin = ProviderOrigin.Extension,
    };
}
