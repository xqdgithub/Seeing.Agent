using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

public sealed class TuiCompletionProviderTests
{
    [Fact]
    public void GetCompletions_Prefix_ShouldReturnMatchingCommands()
    {
        var provider = NewProvider();

        var items = provider.GetCompletions("/s", 2).Select(i => i.Name).ToList();

        items.Should().Contain("/sessions").And.Contain("/scenario").And.NotContain("/model");
    }

    [Fact]
    public void GetCompletions_CaseInsensitivePrefix_ShouldMatch()
    {
        var provider = NewProvider();

        var items = provider.GetCompletions("/SESS", 5).Select(i => i.Name).ToList();

        items.Should().Contain("/sessions");
    }

    [Fact]
    public void GetCompletions_WithTrailingArgument_ShouldReturnEmpty()
    {
        var provider = NewProvider();

        provider.GetCompletions("/model gpt", 10).Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_WithoutSlash_ShouldReturnEmpty()
    {
        var provider = NewProvider();

        provider.GetCompletions("hello", 5).Should().BeEmpty();
        provider.GetCompletions("   ", 3).Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_LeadingWhitespaceBeforeSlash_ShouldStillMatch()
    {
        var provider = NewProvider();

        var items = provider.GetCompletions("   /sess", 8).Select(i => i.Name).ToList();

        items.Should().Contain("/sessions");
    }

    [Fact]
    public void GetCompletions_AliasPrefix_ShouldReturnAliasCandidates()
    {
        var provider = NewProvider();

        var items = provider.GetCompletions("/q", 2).Select(i => i.Name).ToList();

        items.Should().Contain("/q").And.Contain("/quit");
    }

    [Fact]
    public void GetCompletions_ServerCommands_ShouldIncludeVisibleAndExcludeHidden()
    {
        var provider = NewProvider(
            new CommandMetadata { Name = "clear", Description = "清空上下文" },
            new CommandMetadata { Name = "secret", Description = "隐藏命令", IsHidden = true });

        var items = provider.GetCompletions("/c", 2).Select(i => i.Name).ToList();

        items.Should().Contain("/clear").And.NotContain("/secret");
    }

    [Fact]
    public void GetCompletions_ServerCommandDuplicatingLocal_ShouldKeepLocalDescription()
    {
        var provider = NewProvider(new CommandMetadata { Name = "help", Description = "Server help" });

        var items = provider.GetCompletions("/help", 5);

        items.Should().ContainSingle(i => i.Name == "/help")
            .Which.Description.Should().Be("显示本帮助（含服务端命令）");
    }

    [Fact]
    public void GetCompletions_ShouldReturnSortedByName()
    {
        var provider = NewProvider(
            new CommandMetadata { Name = "clear", Description = "清空上下文" },
            new CommandMetadata { Name = "compact", Description = "压缩上下文" });

        var names = provider.GetCompletions("/", 1).Select(i => i.Name).ToList();

        names.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCompletions_ServerCandidate_ShouldUseServerDescription()
    {
        var provider = NewProvider(new CommandMetadata { Name = "clear", Description = "清空上下文" });

        provider.GetCompletions("/clear", 6)
            .Should().ContainSingle(i => i.Name == "/clear" && i.Description == "清空上下文");
    }

    [Fact]
    public void TryApply_UniqueMatch_ShouldCompleteAndPlaceCursor()
    {
        var provider = NewProvider();

        var apply = provider.TryApply("/sess", 5);

        apply.Should().NotBeNull();
        apply!.Value.Text.Should().Be("/sessions ");
        apply.Value.Cursor.Should().Be(10);
    }

    [Fact]
    public void TryApply_ExactCommand_ShouldAppendSpace()
    {
        var provider = NewProvider();

        var apply = provider.TryApply("/model", 6);

        apply.Should().NotBeNull();
        apply!.Value.Text.Should().Be("/model ");
        apply.Value.Cursor.Should().Be(7);
    }

    [Fact]
    public void TryApply_AliasUniqueMatch_ShouldComplete()
    {
        var provider = NewProvider();

        var apply = provider.TryApply("/qui", 4);

        apply.Should().NotBeNull();
        apply!.Value.Text.Should().Be("/quit ");
    }

    [Fact]
    public void TryApply_MultipleMatches_ShouldReturnNull()
    {
        var provider = NewProvider();

        provider.TryApply("/s", 2).Should().BeNull();
        provider.TryApply("/q", 2).Should().BeNull();
    }

    [Fact]
    public void TryApply_NoMatch_ShouldReturnNull()
    {
        var provider = NewProvider();

        provider.TryApply("/zzz", 4).Should().BeNull();
        provider.TryApply("hello", 5).Should().BeNull();
    }

    [Fact]
    public void TryApply_WithTrailingArgument_ShouldPreserveRemainder()
    {
        var provider = NewProvider();

        var apply = provider.TryApply("/resu extra", 5);

        apply.Should().NotBeNull();
        apply!.Value.Text.Should().Be("/resume extra");
        apply.Value.Cursor.Should().Be(8);
    }

    private static TuiCompletionProvider NewProvider(params CommandMetadata[] serverCommands)
    {
        var registry = new Mock<ICommandRegistry>();
        registry.Setup(r => r.GetAllMetadata()).Returns(serverCommands);
        return new TuiCompletionProvider(registry.Object);
    }
}
