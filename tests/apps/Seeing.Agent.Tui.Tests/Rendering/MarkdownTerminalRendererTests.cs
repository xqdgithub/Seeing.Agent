using FluentAssertions;
using Seeing.Agent.Tui.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

public sealed class MarkdownTerminalRendererTests
{
    private static string Render(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 100;
        console.Profile.Height = 40;
        console.Write(renderable);
        return writer.ToString();
    }

    [Fact]
    public void Render_ShouldHandleHeadingsListsTablesAndUnclosedFence_WithoutThrowing()
    {
        const string markdown =
            "# Title\n\n- alpha\n- beta\n\n| c1 | c2 |\n|----|----|\n| 1 | 2 |\n\n```csharp\nvar x = 1;\n```\n\n```\nunclosed\n";

        var renderer = new MarkdownTerminalRenderer();

        var act = () => renderer.Render(markdown, 80);
        act.Should().NotThrow();

        var text = Render(renderer.Render(markdown, 80));
        text.Should().Contain("Title");
        text.Should().Contain("alpha");
        text.Should().Contain("c1");
        text.Should().Contain("unclosed");
    }

    [Fact]
    public void Render_ShouldNotThrow_ForExtremeInput()
    {
        var renderer = new MarkdownTerminalRenderer();
        var cases = new[]
        {
            new string('x', 20000),
            "[[[[",
            "|a|\n",
            "> quote\n",
            string.Empty,
            "   \n",
            new string('[', 500),
            "- \n- \n",
        };

        foreach (var markdown in cases)
        {
            var act = () => renderer.Render(markdown, 40);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Render_NullOrEmpty_ShouldReturnRenderable()
    {
        var renderer = new MarkdownTerminalRenderer();

        renderer.Render(string.Empty, 80).Should().NotBeNull();

        var act = () => renderer.Render(null!, 80);
        act.Should().NotThrow();
    }

    [Fact]
    public void Render_ShouldEscapeMarkupCharacters_WithoutThrowing()
    {
        var renderer = new MarkdownTerminalRenderer();
        var text = Render(renderer.Render("a [bold]not markup[/] b", 60));

        text.Should().Contain("a [bold]not markup[/] b");
    }
}
