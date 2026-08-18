using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Moq;
using Seeing.Agent.App.Execution;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
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
    private readonly IModelManager _modelManager = CreateModelManager();

    [Fact]
    public void ApplyInboundModelAndMode_EmptyFields_ShouldFillFromOptions()
    {
        var session = SessionData.Create();
        session.SelectedModel = string.Empty;
        session.SelectedAcpMode = string.Empty;

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "gpt-4o", "ask", _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ExistingFields_ShouldOverwrite()
    {
        var session = SessionData.Create();
        session.SelectedModel = "gpt-4o-mini";
        session.SelectedAcpMode = "build";

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "claude-sonnet-4-20250514", "ask", _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("claude-sonnet-4-20250514");
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ModelOnly_ShouldUpdateModelOnly()
    {
        var session = SessionData.Create();
        session.SelectedModel = string.Empty;
        session.SelectedAcpMode = "build";

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "gpt-4o", null, _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedAcpMode.Should().Be("build");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ModeOnly_ShouldUpdateModeOnly()
    {
        var session = SessionData.Create();
        session.SelectedModel = "gpt-4o-mini";
        session.SelectedAcpMode = string.Empty;

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, null, "ask", _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o-mini");
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void ApplyInboundModelAndMode_BlankOptions_ShouldNotChange()
    {
        var session = SessionData.Create();
        session.SelectedModel = "gpt-4o";
        session.SelectedAcpMode = "build";

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "  ", null, _modelManager);

        changed.Should().BeFalse();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedAcpMode.Should().Be("build");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ShouldTrimWhitespace()
    {
        var session = SessionData.Create();

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "  gpt-4o  ", "  ask  ", _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedAcpMode.Should().Be("ask");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ShouldUpdateTimestamp()
    {
        var session = SessionData.Create();
        var originalUpdatedAt = session.UpdatedAt;
        session.SelectedModel = string.Empty;

        Thread.Sleep(10);

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "gpt-4o", null, _modelManager);

        changed.Should().BeTrue();
        session.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void ApplyInboundModelAndMode_WithKnownProvider_ShouldStoreFullModelRef()
    {
        var session = SessionData.Create();
        session.SelectedModel = "openai/gpt-4o-mini";

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "openai/gpt-4o", null, _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("openai/gpt-4o");
    }

    [Fact]
    public void ApplyInboundModelAndMode_WithUnknownProvider_ShouldStoreAsModelOnly()
    {
        var session = SessionData.Create();

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "Qwen/Qwen3-VL-Embedding-8B", null, _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("Qwen/Qwen3-VL-Embedding-8B");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ExistingProvider_ShouldUpdateWhenModelUpdated()
    {
        var session = SessionData.Create();
        session.SelectedModel = "seeing-coding-plan/GLM-5";

        var changed = ExecutionJobService.ApplyInboundModelAndMode(session, "openai/gpt-4o", null, _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("openai/gpt-4o");
    }

    private static IModelManager CreateModelManager()
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(c => c.GetModel(It.IsAny<string>())).Returns((string _) => null);
        catalog.Setup(c => c.GetModels()).Returns(new Dictionary<string, ModelConfig>());

        var store = new Mock<IAgentStore>();
        store.Setup(s => s.GetAsync(It.IsAny<string>())).ReturnsAsync(new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native
        });

        return new ModelManager(catalog.Object, store.Object);
    }
}
