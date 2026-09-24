using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.WebUI.Components.Messaging;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering;
using Seeing.Agent.WebUI.Rendering.Components;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Questions;

public class QuestionResultCardRegistrationTests
{
    private static IMessageComponentRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMessageRendering();
        return services.BuildServiceProvider().GetRequiredService<IMessageComponentRegistry>();
    }

    [Fact]
    public void QuestionBlock_ShouldResolveToQuestionMessageComponent()
    {
        var registry = BuildRegistry();
        var block = ContentBlock.CreateToolCall(new ToolCallViewModel { Id = "1", Name = "question" }, 0);

        registry.TryGetComponent(block, out var component).Should().BeTrue();
        component!.GetComponentType().Should().Be(typeof(QuestionMessageComponent));
    }

    [Fact]
    public void NormalToolBlock_ShouldResolveToGenericToolCallComponent()
    {
        var registry = BuildRegistry();
        var block = ContentBlock.CreateToolCall(new ToolCallViewModel { Id = "1", Name = "read" }, 0);

        registry.TryGetComponent(block, out var component).Should().BeTrue();
        component!.GetComponentType().Should().Be(typeof(ToolCallMessageComponent));
    }

    [Fact]
    public void GenericToolCallComponent_ShouldNotRenderQuestionBlock()
    {
        var registry = BuildRegistry();
        var generic = registry.GetAllComponents().Single(c => c.Name == "ToolCall");
        var block = ContentBlock.CreateToolCall(new ToolCallViewModel { Id = "1", Name = "question" }, 0);
        generic.CanRender(block).Should().BeFalse();
    }
}
