using FluentAssertions;
using Seeing.Agent.App.Execution;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ExecutionJobServiceOutboundBackfillTests
{
    [Fact]
    public void TryBackfillSessionOutbound_EmptyFields_ShouldFillFromInbound()
    {
        var session = SessionData.Create();
        session.ChannelId = null;
        session.UserId = null;

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "  qq  ", "  u1  ");

        changed.Should().BeTrue();
        session.ChannelId.Should().Be("qq");
        session.UserId.Should().Be("u1");
    }

    [Fact]
    public void TryBackfillSessionOutbound_ExistingFields_ShouldNotOverwrite()
    {
        var session = SessionData.Create();
        session.ChannelId = "wecom";
        session.UserId = "existing";

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "qq", "u1");

        changed.Should().BeFalse();
        session.ChannelId.Should().Be("wecom");
        session.UserId.Should().Be("existing");
    }

    [Fact]
    public void TryBackfillSessionOutbound_WhitespaceSessionFields_ShouldFill()
    {
        var session = SessionData.Create();
        session.ChannelId = "   ";
        session.UserId = "";

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "qq", "u1");

        changed.Should().BeTrue();
        session.ChannelId.Should().Be("qq");
        session.UserId.Should().Be("u1");
    }

    [Fact]
    public void TryBackfillSessionOutbound_BlankInbound_ShouldNotChange()
    {
        var session = SessionData.Create();
        session.ChannelId = null;
        session.UserId = null;

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "  ", null);

        changed.Should().BeFalse();
        session.ChannelId.Should().BeNull();
        session.UserId.Should().BeNull();
    }

    [Fact]
    public void TryBackfillSessionOutbound_OnlyChannelEmpty_ShouldFillChannelOnly()
    {
        var session = SessionData.Create();
        session.ChannelId = null;
        session.UserId = "keep";

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "qq", "ignored");

        changed.Should().BeTrue();
        session.ChannelId.Should().Be("qq");
        session.UserId.Should().Be("keep");
    }
}

public class ExecutionJobServiceModelSelectionTests
{
    private readonly MockProviderManager _providerManager = new();

    [Fact]
    public void TryBackfillSessionModelSelection_EmptyFields_ShouldFillFromOptions()
    {
        var session = SessionData.Create();
        session.SelectedModel = string.Empty;
        session.SelectedAcpMode = string.Empty;

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "gpt-4o", "ask", _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedModelProvider.Should().BeEmpty();
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void TryBackfillSessionModelSelection_ExistingFields_ShouldOverwrite()
    {
        // Unlike TryBackfillSessionOutbound, model selection should ALWAYS update
        // to reflect user's explicit choice
        var session = SessionData.Create();
        session.SelectedModel = "gpt-4o-mini";
        session.SelectedAcpMode = "build";

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "claude-sonnet-4-20250514", "ask", _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("claude-sonnet-4-20250514");
        session.SelectedModelProvider.Should().BeEmpty();
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void TryBackfillSessionModelSelection_ModelOnly_ShouldUpdateModelOnly()
    {
        var session = SessionData.Create();
        session.SelectedModel = string.Empty;
        session.SelectedAcpMode = "build";

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "gpt-4o", null, _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedModelProvider.Should().BeEmpty();
        session.SelectedAcpMode.Should().Be("build"); // Should preserve existing mode
    }

    [Fact]
    public void TryBackfillSessionModelSelection_ModeOnly_ShouldUpdateModeOnly()
    {
        var session = SessionData.Create();
        session.SelectedModel = "gpt-4o-mini";
        session.SelectedAcpMode = string.Empty;

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, null, "ask", _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o-mini"); // Should preserve existing model
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void TryBackfillSessionModelSelection_BlankOptions_ShouldNotChange()
    {
        var session = SessionData.Create();
        session.SelectedModel = "gpt-4o";
        session.SelectedAcpMode = "build";

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "  ", null, _providerManager);

        changed.Should().BeFalse();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedAcpMode.Should().Be("build");
    }

    [Fact]
    public void TryBackfillSessionModelSelection_ShouldTrimWhitespace()
    {
        var session = SessionData.Create();

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "  gpt-4o  ", "  ask  ", _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void TryBackfillSessionModelSelection_ShouldUpdateTimestamp()
    {
        var session = SessionData.Create();
        var originalUpdatedAt = session.UpdatedAt;
        session.SelectedModel = string.Empty;

        // Ensure time difference
        Thread.Sleep(10);

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "gpt-4o", null, _providerManager);

        changed.Should().BeTrue();
        session.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void TryBackfillSessionModelSelection_WithKnownProvider_ShouldSplitProviderAndModel()
    {
        // When ModelId is in "provider/modelId" format and provider is known,
        // should split into SelectedModelProvider and SelectedModel
        var session = SessionData.Create();
        session.SelectedModel = "gpt-4o-mini";
        session.SelectedModelProvider = "openai";

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "openai/gpt-4o", null, _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedModelProvider.Should().Be("openai");
    }

    [Fact]
    public void TryBackfillSessionModelSelection_WithUnknownProvider_ShouldStoreAsModelOnly()
    {
        // When ModelId is in "provider/modelId" format but provider is NOT known,
        // should store the full string as SelectedModel (HuggingFace style IDs)
        var session = SessionData.Create();

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "Qwen/Qwen3-VL-Embedding-8B", null, _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("Qwen/Qwen3-VL-Embedding-8B");
        session.SelectedModelProvider.Should().BeEmpty();
    }

    [Fact]
    public void TryBackfillSessionModelSelection_ExistingProvider_ShouldUpdateWhenModelUpdated()
    {
        // When updating model, should also update/clear provider accordingly
        var session = SessionData.Create();
        session.SelectedModel = "GLM-5";
        session.SelectedModelProvider = "seeing-coding-plan";

        var changed = ExecutionJobService.TryBackfillSessionModelSelection(session, "openai/gpt-4o", null, _providerManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedModelProvider.Should().Be("openai");
    }
}

/// <summary>
/// Mock IProviderManager for testing
/// </summary>
internal class MockProviderManager : IProviderManager
{
    private readonly Dictionary<string, ProviderConfig> _providers = new()
    {
        ["openai"] = new ProviderConfig { Name = "openai" },
        ["anthropic"] = new ProviderConfig { Name = "anthropic" },
        ["seeing-coding-plan"] = new ProviderConfig { Name = "seeing-coding-plan" }
    };

    public IReadOnlyDictionary<string, ProviderConfig> GetProviders() => _providers;

    public ProviderConfig? GetProvider(string providerId) =>
        _providers.TryGetValue(providerId, out var config) ? config : null;

    public string? GetDefaultProvider() => "openai";

    public ILlmClient? GetClient(string providerId) => null;

    public ILlmClient? GetClientForModel(string modelId) => null;

    public Task<bool> TestConnectionAsync(string providerId, CancellationToken ct = default) => Task.FromResult(true);

    public Task SaveProviderAsync(string providerId, ProviderConfig config, ConfigLevel level = ConfigLevel.Project, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteProviderAsync(string providerId, ConfigLevel level = ConfigLevel.Project, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetDefaultProviderAsync(string? providerId, ConfigLevel level = ConfigLevel.Project, CancellationToken ct = default) => Task.CompletedTask;
}
