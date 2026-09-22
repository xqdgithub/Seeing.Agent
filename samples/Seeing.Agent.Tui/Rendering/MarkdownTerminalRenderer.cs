using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;
using MarkdownTable = Markdig.Extensions.Tables.Table;
using MarkdownTableCell = Markdig.Extensions.Tables.TableCell;
using MarkdownTableRow = Markdig.Extensions.Tables.TableRow;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// Markdig AST → Spectre 原语的容忍式渲染器。任何异常都降级为纯文本，绝不抛出。
/// </summary>
public sealed class MarkdownTerminalRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>解析并映射为 Spectre 段；失败时返回 <see cref="Text"/> 原样文本。</summary>
    public IRenderable Render(string markdown, int width)
    {
        try
        {
            if (string.IsNullOrEmpty(markdown))
                return new Text(string.Empty);

            var document = Markdown.Parse(markdown, _pipeline);
            var items = new List<IRenderable>();
            foreach (var block in document)
                items.Add(RenderBlock(block, width));

            if (items.Count == 0)
                return new Text(markdown);

            return items.Count == 1 ? items[0] : new Rows(items);
        }
        catch
        {
            return new Text(markdown);
        }
    }

    private IRenderable RenderBlock(Block block, int width)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return new Markup($"[bold underline]{InlineMarkup(heading.Inline)}[/]");

            case ParagraphBlock paragraph:
                return paragraph.Inline is null
                    ? new Text(string.Empty)
                    : new Markup(InlineMarkup(paragraph.Inline));

            case ListBlock list:
                return RenderList(list, width, 0);

            case MarkdownTable table:
                return RenderTable(table);

            case QuoteBlock quote:
                return RenderQuote(quote);

            case FencedCodeBlock fenced:
                return RenderCode(fenced.Lines.ToString());

            case CodeBlock code:
                return RenderCode(code.Lines.ToString());

            case ThematicBreakBlock:
                return new Rule();

            default:
                return new Text(BlockToPlainText(block));
        }
    }

    private IRenderable RenderList(ListBlock list, int width, int indent)
    {
        var rows = new List<IRenderable>();
        var order = 1;

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
                continue;

            var marker = list.IsOrdered ? $"{order}. " : $"{TuiGlyphs.Bullet} ";
            var pad = new string(' ', indent * 2);
            var lines = new List<IRenderable>();
            var first = true;

            foreach (var itemChild in item)
            {
                if (itemChild is ListBlock nested)
                {
                    lines.Add(RenderList(nested, width, indent + 1));
                    continue;
                }

                var text = BlockToPlainText(itemChild);
                var prefix = first ? pad + marker : new string(' ', pad.Length + marker.Length);
                lines.Add(new Markup($"{Markup.Escape(prefix)}{Markup.Escape(text)}"));
                first = false;
            }

            if (lines.Count == 0)
                continue;

            rows.Add(lines.Count == 1 ? lines[0] : new Rows(lines));
            order++;
        }

        if (rows.Count == 0)
            return new Text(string.Empty);

        return rows.Count == 1 ? rows[0] : new Rows(rows);
    }

    private IRenderable RenderTable(MarkdownTable table)
    {
        var result = new Spectre.Console.Table();
        var rows = table.OfType<MarkdownTableRow>().ToList();
        var header = rows.FirstOrDefault(r => r.IsHeader);

        var columnCount = header?.Count ?? rows.FirstOrDefault()?.Count ?? 0;
        for (var i = 0; i < columnCount; i++)
        {
            var title = header is not null
                ? CellPlainText(header.ElementAtOrDefault(i) as MarkdownTableCell)
                : string.Empty;
            result.AddColumn(new TableColumn(new Markup($"[bold]{Markup.Escape(title)}[/]")));
        }

        foreach (var row in rows)
        {
            if (ReferenceEquals(row, header))
                continue;

            var cells = row.OfType<MarkdownTableCell>().Select(c => (IRenderable)new Text(CellPlainText(c))).ToArray();
            if (cells.Length > 0)
                result.AddRow(cells);
        }

        return result;
    }

    private IRenderable RenderQuote(QuoteBlock quote)
    {
        var lines = new List<IRenderable>();
        foreach (var child in quote)
        {
            var text = BlockToPlainText(child);
            foreach (var line in text.Split('\n'))
                lines.Add(new Markup($"[grey]│[/] {Markup.Escape(line)}"));
        }

        if (lines.Count == 0)
            return new Text(string.Empty);

        return lines.Count == 1 ? lines[0] : new Rows(lines);
    }

    private static IRenderable RenderCode(string text)
    {
        var panel = new Panel(new Text(text)).Border(BoxBorder.Rounded);
        return panel;
    }

    private static string BlockToPlainText(Block? block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                return InlineText(paragraph.Inline);
            case HeadingBlock heading:
                return InlineText(heading.Inline);
            case FencedCodeBlock fenced:
                return fenced.Lines.ToString();
            case CodeBlock code:
                return code.Lines.ToString();
            case ThematicBreakBlock:
                return "---";
            case QuoteBlock quote:
                return string.Join(' ', quote.Select(BlockToPlainText));
            case null:
                return string.Empty;
            default:
                return block is LeafBlock { Inline: not null } leaf ? InlineText(leaf.Inline) : string.Empty;
        }
    }

    private static string CellPlainText(MarkdownTableCell? cell)
    {
        if (cell is null)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in cell)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(BlockToPlainText(block));
        }

        return sb.ToString();
    }

    private static string InlineText(ContainerInline? container)
    {
        var sb = new StringBuilder();
        AppendInline(sb, container?.FirstChild, markup: false);
        return sb.ToString().TrimEnd();
    }

    private static string InlineMarkup(ContainerInline? container)
    {
        var sb = new StringBuilder();
        AppendInline(sb, container?.FirstChild, markup: true);
        return sb.ToString().TrimEnd();
    }

    private static void AppendInline(StringBuilder sb, Inline? inline, bool markup)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Append(sb, literal.Content.ToString(), markup);
                    break;

                case CodeInline code:
                    if (markup)
                        sb.Append("[grey70 on grey15]").Append(Markup.Escape(code.Content)).Append("[/]");
                    else
                        sb.Append(code.Content);
                    break;

                case LinkInline link:
                    if (link.IsImage)
                    {
                        Append(sb, $"[图片] {link.Label ?? link.Url ?? string.Empty}", markup);
                        break;
                    }

                    if (link.FirstChild is not null)
                        AppendInline(sb, link.FirstChild, markup);
                    else
                        Append(sb, link.Label ?? link.Url ?? string.Empty, markup);

                    if (!string.IsNullOrEmpty(link.Url))
                    {
                        if (markup)
                            sb.Append(" ([blue underline]").Append(Markup.Escape(link.Url)).Append("[/])");
                        else
                            sb.Append(" (").Append(link.Url).Append(')');
                    }
                    break;

                case EmphasisInline emphasis:
                    if (markup)
                        sb.Append("[italic]");
                    AppendInline(sb, emphasis.FirstChild, markup);
                    if (markup)
                        sb.Append("[/]");
                    break;

                case LineBreakInline:
                    sb.Append('\n');
                    break;

                case ContainerInline container:
                    AppendInline(sb, container.FirstChild, markup);
                    break;

                default:
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    private static void Append(StringBuilder sb, string text, bool markup)
        => sb.Append(markup ? Markup.Escape(text) : text);
}
