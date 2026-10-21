using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Prompts;
using Xunit;

namespace Seeing.Agent.Tests.Prompts;

/// <summary>
/// 贡献者自述 Heading + Placement 的机制测试：builder 不认识任何具体分节名。
/// </summary>
public class PromptBuilderPlacementTests
{
    [Fact]
    public async Task ShouldPlaceAfterNamedSection()
    {
        var alpha = new FakeContributor("alpha", 10, "## Alpha", PromptSectionPlacement.AtEnd, "ALPHA-BODY");
        var beta = new FakeContributor("beta", 20, "## Beta", PromptSectionPlacement.After("alpha"), "BETA-BODY");

        // 乱序注册：beta 声明 After("alpha")，应紧随 alpha 块之后
        var builder = new PromptBuilder([beta, alpha]);
        var result = await builder.BuildAsync(NewContext("Base prompt."), TestContext.Current.CancellationToken);

        var baseIdx = result.IndexOf("Base prompt.", StringComparison.Ordinal);
        var alphaIdx = result.IndexOf("## Alpha", StringComparison.Ordinal);
        var alphaBodyIdx = result.IndexOf("ALPHA-BODY", StringComparison.Ordinal);
        var betaIdx = result.IndexOf("## Beta", StringComparison.Ordinal);
        var betaBodyIdx = result.IndexOf("BETA-BODY", StringComparison.Ordinal);

        baseIdx.Should().BeGreaterThan(-1);
        baseIdx.Should().BeLessThan(alphaIdx);
        alphaIdx.Should().BeLessThan(alphaBodyIdx);
        alphaBodyIdx.Should().BeLessThan(betaIdx);
        betaIdx.Should().BeLessThan(betaBodyIdx);
    }

    [Fact]
    public async Task ShouldAppendAtEnd_WhenPlacementIsAppendAtEnd()
    {
        var first = new FakeContributor("aa", 10, "## AA", PromptSectionPlacement.AtEnd, "AA-BODY");
        var second = new FakeContributor("bb", 20, "## BB", PromptSectionPlacement.AtEnd, "BB-BODY");

        // 乱序注册：AtEnd 分节按既有顺序（Order 升序）追加末尾
        var builder = new PromptBuilder([second, first]);
        var result = await builder.BuildAsync(NewContext("Base prompt."), TestContext.Current.CancellationToken);

        var baseIdx = result.IndexOf("Base prompt.", StringComparison.Ordinal);
        var aaIdx = result.IndexOf("## AA", StringComparison.Ordinal);
        var bbIdx = result.IndexOf("## BB", StringComparison.Ordinal);

        baseIdx.Should().BeGreaterThan(-1);
        baseIdx.Should().BeLessThan(aaIdx);
        aaIdx.Should().BeLessThan(bbIdx);
        result.Should().Contain("AA-BODY");
        result.Should().Contain("BB-BODY");
    }

    [Fact]
    public async Task ShouldLogError_WhenHeadingMissing()
    {
        var missing = new FakeContributor("ghost", 10, "", PromptSectionPlacement.AtEnd, "GHOST-BODY");
        var normal = new FakeContributor("ok", 20, "## Ok", PromptSectionPlacement.AtEnd, "OK-BODY");

        var logger = new Mock<ILogger<PromptBuilder>>();
        var builder = new PromptBuilder([missing, normal], logger.Object);

        var act = async () => await builder.BuildAsync(
            NewContext("Base prompt."), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        var result = await builder.BuildAsync(NewContext("Base prompt."), TestContext.Current.CancellationToken);
        result.Should().Contain("OK-BODY");
        result.Should().NotContain("GHOST-BODY");

        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("ghost")),
                It.IsAny<Exception?>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((_, _) => true)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task NewSection_ShouldWorkWithoutBuilderChange()
    {
        // 测试专用新分节 "changelog"：PromptBuilder 无该分节名硬编码，仅凭自述声明即可插入。
        var env = new FakeContributor(
            PromptSectionNames.Environment, 10, "## Environment", PromptSectionPlacement.AtEnd, "ENV-BODY");
        var changelog = new FakeContributor(
            "changelog", 20, "## Changelog",
            PromptSectionPlacement.After(PromptSectionNames.Environment), "CHANGELOG-BODY");

        var builder = new PromptBuilder([env, changelog]);
        var result = await builder.BuildAsync(NewContext("Base prompt."), TestContext.Current.CancellationToken);

        var envIdx = result.IndexOf("## Environment", StringComparison.Ordinal);
        var envBodyIdx = result.IndexOf("ENV-BODY", StringComparison.Ordinal);
        var changelogIdx = result.IndexOf("## Changelog", StringComparison.Ordinal);
        var changelogBodyIdx = result.IndexOf("CHANGELOG-BODY", StringComparison.Ordinal);

        envIdx.Should().BeGreaterThan(-1);
        envBodyIdx.Should().BeGreaterThan(envIdx);
        changelogIdx.Should().BeGreaterThan(envBodyIdx);
        changelogBodyIdx.Should().BeGreaterThan(changelogIdx);
    }

    private static PromptContext NewContext(string systemPrompt) => new()
    {
        Agent = new AgentDefinition
        {
            Name = "test",
            SystemPrompt = systemPrompt,
        }
    };

    private sealed class FakeContributor : IPromptSectionContributor
    {
        private readonly string _body;

        public FakeContributor(
            string sectionName,
            int order,
            string heading,
            PromptSectionPlacement placement,
            string body)
        {
            SectionName = sectionName;
            Order = order;
            Heading = heading;
            Placement = placement;
            _body = body;
        }

        public string SectionName { get; }
        public string Heading { get; }
        public PromptSectionPlacement Placement { get; }
        public int Order { get; }

        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(_body);
    }
}
