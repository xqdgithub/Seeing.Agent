using Acp.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Acp.Session;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpSessionConfigApplierTests
{
    [Fact]
    public async Task ApplyAsync_WithConfigOptions_ShouldSetModeAndModel()
    {
        var fake = new FakeConfigClient();
        var options = new List<SessionConfigOption>
        {
            new()
            {
                Id = "mode",
                Category = SessionConfigOptionCategories.Mode,
                CurrentValue = "ask",
                SelectOptions = new SessionConfigSelectOptions
                {
                    Flat =
                    [
                        new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
                        new SessionConfigSelectOption { Value = "build", Name = "Build" }
                    ]
                }
            },
            new()
            {
                Id = "model",
                Category = SessionConfigOptionCategories.Model,
                CurrentValue = "default",
                SelectOptions = new SessionConfigSelectOptions
                {
                    Flat =
                    [
                        new SessionConfigSelectOption { Value = "default", Name = "Default" },
                        new SessionConfigSelectOption { Value = "gpt-4", Name = "GPT-4" }
                    ]
                }
            }
        };

        var applier = new AcpSessionConfigApplier(NullLogger<AcpSessionConfigApplier>.Instance);

        await applier.ApplyAsync(
            fake,
            "acp-sess",
            options,
            desiredModeId: "build",
            desiredModelId: "gpt-4");

        fake.ConfigSets.Should().BeEquivalentTo([
            ("acp-sess", "mode", "build"),
            ("acp-sess", "model", "gpt-4")
        ]);
    }

    [Fact]
    public async Task ApplyAsync_WhenCurrentValueMatches_ShouldSkipSet()
    {
        var fake = new FakeConfigClient();
        var options = new List<SessionConfigOption>
        {
            new()
            {
                Id = "mode",
                Category = SessionConfigOptionCategories.Mode,
                CurrentValue = "build",
                SelectOptions = new SessionConfigSelectOptions
                {
                    Flat = [new SessionConfigSelectOption { Value = "build", Name = "Build" }]
                }
            }
        };

        var applier = new AcpSessionConfigApplier(NullLogger<AcpSessionConfigApplier>.Instance);

        await applier.ApplyAsync(fake, "acp-sess", options, desiredModeId: "build", desiredModelId: null);

        fake.ConfigSets.Should().BeEmpty();
        fake.Modes.Should().BeEmpty();
        fake.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_WithoutConfigOptions_ShouldUseLegacySetters()
    {
        var fake = new FakeConfigClient();
        var applier = new AcpSessionConfigApplier(NullLogger<AcpSessionConfigApplier>.Instance);

        await applier.ApplyAsync(fake, "acp-sess", null, desiredModeId: "build", desiredModelId: "gpt-4");

        fake.Modes.Count.Should().Be(1);
        fake.Modes[0].Should().Be(("acp-sess", "build"));
        fake.Models.Count.Should().Be(1);
        fake.Models[0].Should().Be(("acp-sess", "gpt-4"));
    }

    private sealed class FakeConfigClient : IAcpSessionConfigClient
    {
        public List<(string SessionId, string OptionId, string Value)> ConfigSets { get; } = new();
        public List<(string SessionId, string ModeId)> Modes { get; } = new();
        public List<(string SessionId, string ModelId)> Models { get; } = new();

        public Task SetConfigOptionAsync(
            string sessionId,
            string configId,
            string value,
            CancellationToken cancellationToken = default)
        {
            ConfigSets.Add((sessionId, configId, value));
            return Task.CompletedTask;
        }

        public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        {
            Modes.Add((sessionId, modeId));
            return Task.CompletedTask;
        }

        public Task SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken = default)
        {
            Models.Add((sessionId, modelId));
            return Task.CompletedTask;
        }
    }
}
