using System.Net;
using System.Text;
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.ModelCatalog.ModelsDev;
using Xunit;

namespace Seeing.Agent.Llm.ModelCapabilities.Tests;

public class ModelsDevCapabilitySourceTests
{
    private const string SampleApiJson = """
        {
          "opencode": {
            "id": "opencode",
            "models": {
              "big-pickle": {
                "id": "big-pickle",
                "name": "Big Pickle",
                "attachment": false,
                "reasoning": true,
                "interleaved": { "field": "reasoning_content" },
                "limit": { "context": 200000, "output": 32000 },
                "modalities": { "input": ["text"], "output": ["text"] },
                "cost": { "input": 0, "output": 0 }
              }
            }
          },
          "deepseek": {
            "id": "deepseek",
            "models": {
              "deepseek-chat": {
                "id": "deepseek-chat",
                "name": "DeepSeek Chat",
                "attachment": true,
                "reasoning": false,
                "limit": { "context": 128000, "output": 8192 },
                "cost": { "input": 0.14, "output": 0.28, "cache_read": 0.01 }
              }
            }
          },
          "OpenAI": {
            "id": "OpenAI",
            "models": {
              "gpt-test": {
                "id": "gpt-test",
                "name": "GPT Test",
                "attachment": true,
                "reasoning": false,
                "limit": { "context": 8000, "output": 1000 }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task Load_UsesBuiltinWithoutWritingCatalog()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new ModelsDevCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            File.Exists(Path.Combine(dir, "catalog.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "local.json")).Should().BeFalse();

            var entries = await source.ListEntriesAsync(TestContext.Current.CancellationToken);
            entries.Should().Contain(e =>
                e.ProviderId == "deepseek" &&
                e.ModelId == "deepseek-v4-flash" &&
                e.Options!.Thinking!.Levels!.Count >= 2);
            entries.Should().Contain(e =>
                e.ProviderId == "opencode-zen" &&
                e.ModelId == "big-pickle" &&
                e.Limit!.Context == 1_000_000);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task RefreshAsync_WritesRemoteOnly_PreservesLocalAlias_AndBuiltinLevels()
    {
        var dir = CreateTempDir();
        try
        {
            using var http = CreateStubHttp(SampleApiJson);
            var source = new ModelsDevCapabilitySource(dir, httpClient: http);

            await source.LoadAsync(TestContext.Current.CancellationToken);
            await source.UpsertAliasAsync(new ModelCapabilityAlias
            {
                ProviderId = "opencode-zen",
                FromModelId = "alias-from",
                ToModelId = "big-pickle"
            }, TestContext.Current.CancellationToken);

            ModelCapabilitySourceChangedEventArgs? changed = null;
            source.Changed += (_, e) => changed = e;

            await source.RefreshAsync(TestContext.Current.CancellationToken);

            changed.Should().NotBeNull();
            changed!.Kind.Should().Be(ModelCapabilitySourceChangeKind.Reloaded);
            File.Exists(Path.Combine(dir, "remote.json")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "remote.json.bk")).Should().BeFalse(); // first write, no prior
            File.Exists(Path.Combine(dir, "local.json")).Should().BeTrue();

            var entries = await source.ListEntriesAsync(TestContext.Current.CancellationToken);
            var bigPickle = entries.Single(e =>
                e.ProviderId == "opencode-zen" && e.ModelId == "big-pickle");
            // remote limit 200k 被 builtin 1M 覆盖
            bigPickle.Limit!.Context.Should().Be(1_000_000);
            bigPickle.Options!.Thinking!.Supported.Should().BeTrue();
            bigPickle.Options.Thinking.Interleaved.Should().Be("reasoning_content");
            bigPickle.Options.Thinking.Levels.Should().NotBeNull().And.HaveCount(3);

            entries.Should().Contain(e =>
                e.ProviderId == "deepseek" && e.ModelId == "deepseek-chat");
            entries.Should().Contain(e =>
                e.ProviderId == "openai" && e.ModelId == "gpt-test");

            var aliases = await source.ListAliasesAsync(TestContext.Current.CancellationToken);
            aliases.Should().ContainSingle(a =>
                a.FromModelId == "alias-from" && a.ToModelId == "big-pickle");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task LocalOverride_WinsOverBuiltin_AfterRefresh()
    {
        var dir = CreateTempDir();
        try
        {
            using var http = CreateStubHttp(SampleApiJson);
            var source = new ModelsDevCapabilitySource(dir, httpClient: http);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            await source.UpsertEntryAsync(new ModelCapabilityEntry
            {
                ProviderId = "opencode-zen",
                ModelId = "big-pickle",
                Name = "Local Pickle",
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

            await source.RefreshAsync(TestContext.Current.CancellationToken);

            var entry = (await source.ListEntriesAsync(TestContext.Current.CancellationToken))
                .Single(e => e.ModelId == "big-pickle");
            entry.Name.Should().Be("Local Pickle");
            entry.Limit!.Context.Should().Be(42);
            entry.Options!.Thinking!.Levels!.Should().HaveCount(2);
            entry.Options.Thinking.Levels![1].Key.Should().Be("high");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task UpsertEntry_WritesLocalWithBackup()
    {
        var dir = CreateTempDir();
        try
        {
            var source = new ModelsDevCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            await source.UpsertEntryAsync(new ModelCapabilityEntry
            {
                ProviderId = "p1",
                ModelId = "m1",
                Name = "Model One"
            }, TestContext.Current.CancellationToken);

            var localPath = Path.Combine(dir, "local.json");
            var backupPath = localPath + ".bk";
            File.Exists(localPath).Should().BeTrue();
            File.Exists(backupPath).Should().BeFalse();

            await source.UpsertEntryAsync(new ModelCapabilityEntry
            {
                ProviderId = "p1",
                ModelId = "m1",
                Name = "Model One Updated"
            }, TestContext.Current.CancellationToken);

            File.Exists(backupPath).Should().BeTrue();
            File.ReadAllText(backupPath).Should().Contain("Model One");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void MapProviderId_MapsOpenCodeAndLowercases()
    {
        ModelsDevApiMapper.MapProviderId("opencode").Should().Be("opencode-zen");
        ModelsDevApiMapper.MapProviderId("deepseek").Should().Be("deepseek");
        ModelsDevApiMapper.MapProviderId("OpenAI").Should().Be("openai");
    }

    [Fact]
    public void Map_ReasoningTrue_DoesNotInventLevels()
    {
        var doc = ModelsDevApiMapper.Map(SampleApiJson);
        var big = doc.Entries.Single(e => e.ModelId == "big-pickle");
        big.Options!.Thinking!.Supported.Should().BeTrue();
        big.Options.Thinking.Levels.Should().BeNullOrEmpty();
        big.Options.Thinking.Interleaved.Should().Be("reasoning_content");
    }

    [Fact]
    public async Task RefreshAsync_HttpFailure_KeepsLocalAndRaisesRefreshFailed()
    {
        var dir = CreateTempDir();
        try
        {
            using var http = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError)));
            var source = new ModelsDevCapabilitySource(dir, httpClient: http);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            await source.UpsertEntryAsync(new ModelCapabilityEntry
            {
                ProviderId = "local",
                ModelId = "keep-me",
                Name = "Keep"
            }, TestContext.Current.CancellationToken);

            ModelCapabilitySourceChangedEventArgs? changed = null;
            source.Changed += (_, e) => changed = e;

            var act = async () => await source.RefreshAsync();
            await act.Should().ThrowAsync<HttpRequestException>();

            changed.Should().NotBeNull();
            changed!.Kind.Should().Be(ModelCapabilitySourceChangeKind.RefreshFailed);

            var entries = await source.ListEntriesAsync(TestContext.Current.CancellationToken);
            entries.Should().Contain(e => e.ModelId == "keep-me");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task Migrate_LegacyCatalogJson_BecomesLocal()
    {
        var dir = CreateTempDir();
        try
        {
            var legacy = """
                {
                  "entries": [
                    {
                      "providerId": "deepseek",
                      "modelId": "deepseek-v4-flash",
                      "name": "Migrated Flash",
                      "limit": { "context": 123, "output": 456 }
                    }
                  ],
                  "aliases": []
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(dir, "catalog.json"), legacy, TestContext.Current.CancellationToken);

            var source = new ModelsDevCapabilitySource(dir);
            await source.LoadAsync(TestContext.Current.CancellationToken);

            File.Exists(Path.Combine(dir, "local.json")).Should().BeTrue();
            var entry = (await source.ListEntriesAsync(TestContext.Current.CancellationToken))
                .Single(e => e.ModelId == "deepseek-v4-flash");
            entry.Name.Should().Be("Migrated Flash");
            entry.Limit!.Context.Should().Be(123);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    private static HttpClient CreateStubHttp(string json)
        => new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modelsdev-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore cleanup races on Windows
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
