using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.ModelCatalog.Builtin;
using Xunit;

namespace Seeing.Agent.Llm.ModelCapabilities.Tests;

public class BuiltinCapabilitySourceTests
{
    [Fact]
    public async Task Load_HasDeepSeekAndZenWithThinkingLevels()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new BuiltinCapabilitySource(dir);
            await source.LoadAsync();

            var entries = await source.ListEntriesAsync();
            entries.Should().Contain(e =>
                e.ProviderId == "deepseek" &&
                e.ModelId == "deepseek-v4-flash" &&
                e.Options!.Thinking!.Levels!.Count >= 2);
            entries.Should().Contain(e =>
                e.ProviderId == "opencode-zen" &&
                e.ModelId == "big-pickle" &&
                e.Limit!.Context == 1_000_000);
            entries.Should().Contain(e => e.ProviderId == "zai" && e.ModelId == "glm-5.3");
            entries.Should().Contain(e => e.ProviderId == "moonshotai" && e.ModelId == "kimi-k3");
            entries.Should().Contain(e => e.ProviderId == "minimax" && e.ModelId == "MiniMax-M3");
            entries.Should().Contain(e => e.ProviderId == "openai" && e.ModelId == "gpt-6-astra");
            entries.Should().Contain(e => e.ProviderId == "anthropic" && e.ModelId == "claude-opus-5");
            entries.Should().Contain(e => e.ProviderId == "alibaba" && e.ModelId == "qwen3.8-max");
            entries.Should().NotContain(e => e.ModelId == "kimi-k2.5");

            // 体量精简：不应是 models.dev 全量
            entries.Count.Should().BeLessThan(80);
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
            await source.LoadAsync();

            await source.UpsertEntryAsync(new ModelCapabilityEntry
            {
                ProviderId = "deepseek",
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
            });

            var entry = await source.TryGetAsync("deepseek", "deepseek-v4-flash");
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
            await source.LoadAsync();

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
            });

            var entry = await source.TryGetAsync("opencode-zen", "brand-new-model-free");
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
