using FluentAssertions;
using Seeing.Agent.Tui.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

public sealed class TailClipRenderableTests
{
    private static IAnsiConsole CreateConsole(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 120;
        console.Profile.Height = 40;
        return console;
    }

    private static string[] RenderLines(IRenderable renderable)
    {
        var writer = new StringWriter();
        CreateConsole(writer).Write(renderable);
        return writer.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }

    private static IRenderable Lines(int count)
    {
        var items = new List<IRenderable>();
        for (var i = 1; i <= count; i++)
            items.Add(new Text($"line{i}"));
        return new Rows(items);
    }

    [Fact]
    public void Render_WhenContentExceedsMaxLines_ShouldKeepLastLines()
    {
        var clip = new TailClipRenderable(Lines(20), 5);

        var lines = RenderLines(clip);

        lines.Should().HaveCount(5);
        lines.Should().Equal("line16", "line17", "line18", "line19", "line20");
    }

    [Fact]
    public void Render_WhenMaxLinesAtLeastContent_ShouldKeepAll()
    {
        var clip = new TailClipRenderable(Lines(3), 10);

        var lines = RenderLines(clip);

        lines.Should().Equal("line1", "line2", "line3");
    }

    [Fact]
    public void Render_WhenMaxLinesIsOne_ShouldKeepLastLine()
    {
        var clip = new TailClipRenderable(Lines(4), 1);

        var lines = RenderLines(clip);

        lines.Should().Equal("line4");
    }

    [Fact]
    public void Measure_ShouldCapMaxAtMaxLines()
    {
        var clip = new TailClipRenderable(Lines(20), 5);
        var console = CreateConsole(new StringWriter());
        var options = new RenderOptions(console.Profile.Capabilities, new Size(120, 40));

        var measurement = clip.Measure(options, 120);

        measurement.Max.Should().Be(5);
    }
}
