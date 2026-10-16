using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ExecutionRunnerOutboundBackfillTests
{
    [Fact]
    public void TryBackfillChannelRoute_Empty_ShouldFillFromInbound()
    {
        var session = SessionData.Create();
        session.ChannelRoute = null;

        var changed = ExecutionRunner.TryBackfillChannelRoute(
            session, Route("  qq  ", " group ", "  gid  ", "  room  "));

        changed.Should().BeTrue();
        session.ChannelRoute!.ChannelId.Should().Be("qq");
        session.ChannelRoute.Kind.Should().Be("group");
        session.ChannelRoute.ExternalId.Should().Be("gid");
        session.ChannelRoute.Title.Should().Be("room");
    }

    [Fact]
    public void TryBackfillChannelRoute_Existing_ShouldNotOverwrite()
    {
        var session = SessionData.Create();
        session.ChannelRoute = Route("wecom", "direct", "existing");

        var changed = ExecutionRunner.TryBackfillChannelRoute(session, Route("qq", "group", "u1"));

        changed.Should().BeFalse();
        session.ChannelRoute!.ChannelId.Should().Be("wecom");
        session.ChannelRoute.Kind.Should().Be("direct");
        session.ChannelRoute.ExternalId.Should().Be("existing");
    }

    [Fact]
    public void TryBackfillChannelRoute_WhitespaceSessionRoute_ShouldFill()
    {
        var session = SessionData.Create();
        session.ChannelRoute = Route("   ", "direct", "");

        var changed = ExecutionRunner.TryBackfillChannelRoute(session, Route("qq", "group", "u1"));

        changed.Should().BeTrue();
        session.ChannelRoute!.ChannelId.Should().Be("qq");
        session.ChannelRoute.ExternalId.Should().Be("u1");
    }

    [Fact]
    public void TryBackfillChannelRoute_BlankInbound_ShouldNotChange()
    {
        var session = SessionData.Create();
        session.ChannelRoute = null;

        var changed = ExecutionRunner.TryBackfillChannelRoute(session, Route("  ", "group", "u1"));

        changed.Should().BeFalse();
        session.ChannelRoute.Should().BeNull();

        changed = ExecutionRunner.TryBackfillChannelRoute(session, null);
        changed.Should().BeFalse();
        session.ChannelRoute.Should().BeNull();
    }

    private static SessionChannelRoute Route(string channelId, string kind, string externalId, string? title = null)
        => new()
        {
            ChannelId = channelId,
            Kind = kind,
            ExternalId = externalId,
            Title = title
        };
}

public class ExecutionRunnerModelSelectionTests
{
    private readonly IModelManager _modelManager = CreateModelManager();

    [Fact]
    public void ApplyInboundModelAndMode_EmptyFields_ShouldFillFromOptions()
    {
        var session = SessionData.Create();
        session.SelectedModel = string.Empty;
        session.SelectedAcpMode = string.Empty;

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "gpt-4o", "ask", _modelManager);

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

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "claude-sonnet-4-20250514", "ask", _modelManager);

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

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "gpt-4o", null, _modelManager);

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

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, null, "ask", _modelManager);

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

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "  ", null, _modelManager);

        changed.Should().BeFalse();
        session.SelectedModel.Should().Be("gpt-4o");
        session.SelectedAcpMode.Should().Be("build");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ShouldTrimWhitespace()
    {
        var session = SessionData.Create();

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "  gpt-4o  ", "  ask  ", _modelManager);

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

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "gpt-4o", null, _modelManager);

        changed.Should().BeTrue();
        session.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void ApplyInboundModelAndMode_WithKnownProvider_ShouldStoreFullModelRef()
    {
        var session = SessionData.Create();
        session.SelectedModel = "openai/gpt-4o-mini";

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "openai/gpt-4o", null, _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("openai/gpt-4o");
    }

    [Fact]
    public void ApplyInboundModelAndMode_WithUnknownProvider_ShouldStoreAsModelOnly()
    {
        var session = SessionData.Create();

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "Qwen/Qwen3-VL-Embedding-8B", null, _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("Qwen/Qwen3-VL-Embedding-8B");
    }

    [Fact]
    public void ApplyInboundModelAndMode_ExistingProvider_ShouldUpdateWhenModelUpdated()
    {
        var session = SessionData.Create();
        session.SelectedModel = "seeing-coding-plan/GLM-5";

        var changed = ExecutionRunner.ApplyInboundModelAndMode(session, "openai/gpt-4o", null, _modelManager);

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("openai/gpt-4o");
    }

    private static IModelManager CreateModelManager()
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(c => c.GetModel(It.IsAny<string>())).Returns((string _) => null);
        catalog.Setup(c => c.GetModels()).Returns(new Dictionary<string, ModelConfig>());

        var store = new Mock<IAgentStore>();
        store.Setup(s => s.Get(It.IsAny<string>())).Returns(new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native
        });

        return new ModelManager(catalog.Object, store.Object);
    }
}
