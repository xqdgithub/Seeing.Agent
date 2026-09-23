using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.ModelCatalog.Builtin;
using Xunit;

namespace Seeing.Agent.Llm.ModelCapabilities.Tests;

public class BuiltinCapabilitySourceTests
{
    [Fact]
    public async Task Load_HasGenericAndZenScopedEntries()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new BuiltinCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            var entries = await source.ListEntriesAsync(TestContext.Current.CancellationToken);
            entries.Should().Contain(e =>
                string.IsNullOrWhiteSpace(e.ProviderId) &&
                e.ModelId == "deepseek-v4-flash" &&
                e.Options!.Thinking!.Levels!.Count >= 2);
            entries.Should().Contain(e =>
                e.ProviderId == "opencode-zen" &&
                e.ModelId == "big-pickle" &&
                e.Limit!.Context == 1_000_000);
            entries.Should().Contain(e => string.IsNullOrWhiteSpace(e.ProviderId) && e.ModelId == "glm-5.3");
            entries.Should().Contain(e => string.IsNullOrWhiteSpace(e.ProviderId) && e.ModelId == "kimi-k3");
            entries.Should().Contain(e => string.IsNullOrWhiteSpace(e.ProviderId) && e.ModelId == "MiniMax-M3");
            entries.Should().Contain(e => string.IsNullOrWhiteSpace(e.ProviderId) && e.ModelId == "gpt-6-astra");
            entries.Should().Contain(e => string.IsNullOrWhiteSpace(e.ProviderId) && e.ModelId == "claude-opus-5");
            entries.Should().Contain(e => string.IsNullOrWhiteSpace(e.ProviderId) && e.ModelId == "qwen3.8-max");
            entries.Should().NotContain(e => e.ModelId == "kimi-k2.5");

            // 除 Zen 外不得绑死 Provider（通用匹配）
            entries.Where(e => !string.Equals(e.ProviderId, "opencode-zen", StringComparison.OrdinalIgnoreCase))
                .Should().OnlyContain(e => string.IsNullOrWhiteSpace(e.ProviderId));

            // 体量精简：不应是 models.dev 全量
            entries.Count.Should().BeLessThan(80);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task TryGet_GenericEntry_MatchesAnyProviderId()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new BuiltinCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            var viaProxy = await source.TryGetAsync("my-openai-proxy", "glm-5.3", TestContext.Current.CancellationToken);
            var viaZai = await source.TryGetAsync("zai", "glm-5.3", TestContext.Current.CancellationToken);
            var viaEmpty = await source.TryGetAsync("", "claude-opus-5", TestContext.Current.CancellationToken);

            viaProxy.Should().NotBeNull();
            viaProxy!.Limit!.Context.Should().Be(1_000_000);
            viaZai!.ModelId.Should().Be("glm-5.3");
            viaEmpty!.ModelId.Should().Be("claude-opus-5");

            // Zen 私有条目仍需 Provider 对齐，不能被其它实现误命中
            var zenMiss = await source.TryGetAsync("openai", "big-pickle", TestContext.Current.CancellationToken);
            zenMiss.Should().BeNull();
            var zenHit = await source.TryGetAsync("opencode-zen", "big-pickle", TestContext.Current.CancellationToken);
            zenHit.Should().NotBeNull();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task LocalOverride_WinsOverEmbedded()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new BuiltinCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            await source.UpsertEntryAsync(new ModelCapabilityEntry
            {
                ProviderId = null,
                ModelId = "deepseek-v4-flash",
                Name = "Local Flash",
                Limit = new ModelCapabilityLimits { Context = 42, Output = 7 },
                Options = new ModelOptions
                {
                    Thinking = new ThinkingOptions
                    {
                        Supported = true,
                        Levels =
                        [
                            new ThinkingLevel { Key = "disabled", Label = "关" },
                            new ThinkingLevel { Key = "high", Label = "高" }
                        ]
                    }
                }
            }, TestContext.Current.CancellationToken);

            var entry = await source.TryGetAsync("any-provider", "deepseek-v4-flash", TestContext.Current.CancellationToken);
            entry!.Name.Should().Be("Local Flash");
            entry.Limit!.Context.Should().Be(42);
            entry.Options!.Thinking!.Levels!.Should().HaveCount(2);

            File.Exists(Path.Combine(dir, "local.json")).Should().BeTrue();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task TryGet_FreeModel_FallsBackToNonFreeId()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new BuiltinCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            await source.UpsertEntryAsync(new ModelCapabilityEntry
            {
                ProviderId = "opencode-zen",
                ModelId = "brand-new-model",
                Name = "Brand New",
                Limit = new ModelCapabilityLimits { Context = 111111, Output = 2222 },
                Options = new ModelOptions
                {
                    Thinking = new ThinkingOptions
                    {
                        Supported = true,
                        Levels = [new ThinkingLevel { Key = "high", Label = "高" }]
                    }
                }
            }, TestContext.Current.CancellationToken);

            var entry = await source.TryGetAsync("opencode-zen", "brand-new-model-free", TestContext.Current.CancellationToken);
            entry.Should().NotBeNull();
            entry!.ModelId.Should().Be("brand-new-model");
            entry.Limit!.Context.Should().Be(111111);
            entry.Options!.Thinking!.Levels.Should().ContainSingle(l => l.Key == "high");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task TryGet_GenericAlias_ResolvesAcrossProviders()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new BuiltinCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            var entry = await source.TryGetAsync("custom-deepseek", "deepseek-flash", TestContext.Current.CancellationToken);
            entry.Should().NotBeNull();
            entry!.ModelId.Should().Be("deepseek-v4-flash");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "builtin-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
