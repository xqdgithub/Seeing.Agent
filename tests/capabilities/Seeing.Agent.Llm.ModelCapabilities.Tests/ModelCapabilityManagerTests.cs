using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Xunit;

namespace Seeing.Agent.Llm.ModelCapabilities.Tests;

public class ModelCapabilityManagerTests
{
    [Fact]
    public async Task TryEnrich_多源按Order级联FillEmpty()
    {
        var registry = new ModelCapabilitySourceRegistry();
        registry.Register(new FakeSource(
            "low",
            orderHint: 10,
            entry: new ModelCapabilityEntry
            {
                ModelId = "m1",
                Name = "FromLow",
                Limit = new ModelCapabilityLimits { Context = 100000, Output = 1000 }
            }));
        registry.Register(new FakeSource(
            "high",
            orderHint: 0,
            entry: new ModelCapabilityEntry
            {
                ModelId = "m1",
                Name = "FromHigh",
                Limit = new ModelCapabilityLimits { Context = 50000 }
            }));

        var options = new ModelCapabilitiesOptions
        {
            Enabled = true,
            Providers = ["*"],
            Sources =
            [
                new ModelCapabilitySourceOptions { Id = "high", Order = 0, Enabled = true },
                new ModelCapabilitySourceOptions { Id = "low", Order = 10, Enabled = true }
            ]
        };

        using var manager = CreateManager(options, registry);
        var model = new ModelConfig
        {
            Id = "m1",
            Provider = "p1",
            Limit = new ModelLimits { Context = 4096, Output = 4096 }
        };

        var enriched = await manager.TryEnrichIfEnabledAsync(model, TestContext.Current.CancellationToken);

        enriched.Name.Should().Be("FromHigh");
        enriched.Limit.Context.Should().Be(50000);
        enriched.Limit.Output.Should().Be(1000);
    }

    [Fact]
    public async Task TryEnrich_EnabledFalse原样返回()
    {
        var registry = new ModelCapabilitySourceRegistry();
        registry.Register(new FakeSource(
            "s1",
            0,
            new ModelCapabilityEntry { ModelId = "m1", Name = "X" }));

        var options = new ModelCapabilitiesOptions
        {
            Enabled = false,
            Sources = [new ModelCapabilitySourceOptions { Id = "s1", Order = 0, Enabled = true }]
        };

        using var manager = CreateManager(options, registry);
        var model = new ModelConfig { Id = "m1", Provider = "p1", Name = null };

        var enriched = await manager.TryEnrichIfEnabledAsync(model, TestContext.Current.CancellationToken);
        enriched.Name.Should().BeNull();
    }

    [Fact]
    public async Task TryEnrich_Providers白名单门闸()
    {
        var registry = new ModelCapabilitySourceRegistry();
        registry.Register(new FakeSource(
            "s1",
            0,
            new ModelCapabilityEntry { ModelId = "m1", Name = "Enriched" }));

        var options = new ModelCapabilitiesOptions
        {
            Enabled = true,
            Providers = ["allowed"],
            Sources = [new ModelCapabilitySourceOptions { Id = "s1", Order = 0, Enabled = true }]
        };

        using var manager = CreateManager(options, registry);

        var blocked = await manager.TryEnrichIfEnabledAsync(new ModelConfig
        {
            Id = "m1",
            Provider = "other",
            Name = null
        }, TestContext.Current.CancellationToken);
        blocked.Name.Should().BeNull();

        var allowed = await manager.TryEnrichIfEnabledAsync(new ModelConfig
        {
            Id = "m1",
            Provider = "allowed",
            Name = null
        }, TestContext.Current.CancellationToken);
        allowed.Name.Should().Be("Enriched");
    }

    [Fact]
    public async Task NotifyChangedAsync_PublishesModelCapabilitiesChange()
    {
        var registry = new ModelCapabilitySourceRegistry();
        var bus = new CapturingReloadBus();
        var options = new ModelCapabilitiesOptions { Enabled = true, Providers = ["*"] };
        using var manager = CreateManager(options, registry, bus);

        await manager.NotifyChangedAsync(
            ModelCapabilitiesChangeReason.SourceDataChanged,
            affectedSourceIds: ["modelsdev"],
            sourceKind: ModelCapabilitySourceChangeKind.Reloaded, cancellationToken: TestContext.Current.CancellationToken);

        var published = bus.Signals.OfType<ModelCapabilitiesChange>().Should().ContainSingle().Subject;
        published.Reason.Should().Be(ModelCapabilitiesChangeReason.SourceDataChanged);
        published.AffectedSourceIds.Should().Equal("modelsdev");
        published.SourceKind.Should().Be(ModelCapabilitySourceChangeKind.Reloaded);
    }

    [Fact]
    public async Task SourceUnregister_NotifiesSourceUnregistered()
    {
        var registry = new ModelCapabilitySourceRegistry();
        var bus = new CapturingReloadBus();
        var options = new ModelCapabilitiesOptions { Enabled = true, Providers = ["*"] };
        using var manager = CreateManager(options, registry, bus);

        registry.Register(new FakeSource(
            "s1",
            0,
            new ModelCapabilityEntry { ModelId = "m1", Name = "X" }));

        await WaitUntilAsync(() =>
            bus.Signals.OfType<ModelCapabilitiesChange>()
                .Any(c => c.Reason == ModelCapabilitiesChangeReason.SourceRegistered));

        bus.Clear();
        registry.Unregister("s1");

        await WaitUntilAsync(() =>
            bus.Signals.OfType<ModelCapabilitiesChange>()
                .Any(c => c.Reason == ModelCapabilitiesChangeReason.SourceUnregistered));

        bus.Signals.OfType<ModelCapabilitiesChange>().Should().Contain(c =>
            c.Reason == ModelCapabilitiesChangeReason.SourceUnregistered &&
            c.AffectedSourceIds.Contains("s1"));
    }

    [Fact]
    public async Task TryEnrich_FreeSuffix_FallsBackToNonFreeModelId()
    {
        var registry = new ModelCapabilitySourceRegistry();
        registry.Register(new FakeSource(
            "s1",
            0,
            new ModelCapabilityEntry
            {
                ModelId = "deepseek-v4-flash",
                ProviderId = "opencode-zen",
                Limit = new ModelCapabilityLimits { Context = 200000, Output = 128000 },
                Options = new ModelOptions
                {
                    Thinking = new ThinkingOptions
                    {
                        Supported = true,
                        Levels =
                        [
                            new ThinkingLevel { Key = "disabled", Label = "关闭" },
                            new ThinkingLevel { Key = "high", Label = "高" }
                        ]
                    }
                }
            }));

        var options = new ModelCapabilitiesOptions
        {
            Enabled = true,
            Providers = ["*"],
            Sources = [new ModelCapabilitySourceOptions { Id = "s1", Order = 0, Enabled = true }]
        };

        using var manager = CreateManager(options, registry);
        var model = new ModelConfig
        {
            Id = "deepseek-v4-flash-free",
            Provider = "opencode-zen",
            Limit = new ModelLimits { Context = 4096, Output = 4096 }
        };

        var enriched = await manager.TryEnrichIfEnabledAsync(model, TestContext.Current.CancellationToken);
        enriched.Limit.Context.Should().Be(200000);
        enriched.Options!.Thinking!.Levels.Should().HaveCount(2);
    }

    [Fact]
    public void TryGetNonFreeFallbackId_StripsFreeSuffix()
    {
        ModelCapabilityModelIds.TryGetNonFreeFallbackId("foo-free", out var baseId).Should().BeTrue();
        baseId.Should().Be("foo");
        ModelCapabilityModelIds.TryGetNonFreeFallbackId("FOO-FREE", out baseId).Should().BeTrue();
        baseId.Should().Be("FOO");
        ModelCapabilityModelIds.TryGetNonFreeFallbackId("foo", out _).Should().BeFalse();
    }

    private static ModelCapabilityManager CreateManager(
        ModelCapabilitiesOptions options,
        IModelCapabilitySourceRegistry registry,
        IReloadSignalBus? bus = null)
    {
        var monitor = new StaticOptionsMonitor<ModelCapabilitiesOptions>(options);
        return new ModelCapabilityManager(monitor, registry, bus ?? new NoopReloadBus());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var start = Environment.TickCount64;
        while (!predicate())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("条件等待超时");
            await Task.Delay(20);
        }
    }

    private sealed class FakeSource : IModelCapabilitySource
    {
        private readonly ModelCapabilityEntry _entry;

        public FakeSource(string id, int orderHint, ModelCapabilityEntry entry)
        {
            Id = id;
            DisplayName = id;
            _ = orderHint;
            _entry = entry;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public event EventHandler<ModelCapabilitySourceChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public ModelCapabilitySourceStatus GetStatus() => new() { EntryCount = 1 };

        public ValueTask<ModelCapabilityEntry?> TryGetAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(modelId, _entry.ModelId, StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<ModelCapabilityEntry?>(null);
            return ValueTask.FromResult<ModelCapabilityEntry?>(_entry);
        }

        public string ResolveAlias(string providerId, string modelId) => modelId;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class NoopReloadBus : IReloadSignalBus
    {
        public Task<IReadOnlyList<ReloadResult>> PublishAsync(
            IReloadSignal signal,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReloadResult>>([]);
    }

    private sealed class CapturingReloadBus : IReloadSignalBus
    {
        private readonly ConcurrentBag<IReloadSignal> _signals = [];

        public IReadOnlyCollection<IReloadSignal> Signals => _signals;

        public void Clear() => _signals.Clear();

        public Task<IReadOnlyList<ReloadResult>> PublishAsync(
            IReloadSignal signal,
            CancellationToken ct = default)
        {
            _signals.Add(signal);
            return Task.FromResult<IReadOnlyList<ReloadResult>>([]);
        }
    }
}
