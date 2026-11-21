using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Sharpcode;

internal sealed class MarkdownPreview(Func<string, Task> openLink, Func<string, string, Control?> image)
{
    public const int CharacterLimit = 200_000;
    private const int BlockLimit = 450;
    private static readonly FontFamily Mono = new("Cascadia Code, Menlo, DejaVu Sans Mono, monospace");
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().Build();
    private int remainingBlocks = BlockLimit;
    private int remainingInlines = 8_000;

    public static MarkdownDocument Parse(string text) => Markdown.Parse(text, Pipeline);

    public void Render(Panel panel, MarkdownDocument document, bool truncated)
    {
        AddBlocks(panel, document);
        if (truncated || remainingBlocks <= 0 || remainingInlines <= 0)
            panel.Children.Add(new TextBlock { Text = "Preview limited for this large document.", Classes = { "muted" } });
        else if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "This Markdown file is empty.", Classes = { "muted" } });
    }

    private void AddBlocks(Panel panel, ContainerBlock blocks, int depth = 0)
    {
        if (depth > 24)
            return;
        foreach (var block in blocks)
        {
            if (remainingBlocks-- <= 0)
                return;
            var control = block switch
            {
                HeadingBlock heading => Text(heading.Inline, Math.Max(15, 34 - 4 * heading.Level), heading: true),
                ParagraphBlock { Inline.FirstChild: LinkInline { IsImage: true, NextSibling: null } link } paragraph =>
                    image(link.Url ?? "", InlineLabel(link)) ?? Text(paragraph.Inline),
                ParagraphBlock paragraph => Text(paragraph.Inline),
                CodeBlock code => Box(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new SelectableTextBlock { Text = code.Lines.ToString(), FontFamily = Mono, FontSize = 13 }
                }),
                QuoteBlock quote => Quote(quote, depth),
                ListBlock list => List(list, depth),
                Table table => RenderTable(table, depth),
                ThematicBreakBlock => new Border { Height = 1, Background = App.Brush("Divider"), Margin = new Thickness(0, 5) },
                _ => null
            };
            if (control is not null)
                panel.Children.Add(control);
        }
    }

    private Control Quote(QuoteBlock block, int depth)
    {
        var content = new StackPanel { Spacing = 6 };
        AddBlocks(content, block, depth + 1);
        return new Border { BorderBrush = App.Brush("AccentInk"), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 3), Child = content };
    }

    private Control List(ListBlock block, int depth)
    {
        var list = new StackPanel { Spacing = 5 };
        var index = int.TryParse(block.OrderedStart, out var start) ? start : 1;
        foreach (var item in block.OfType<ListItemBlock>())
        {
            if (remainingBlocks-- <= 0)
                break;
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = block.IsOrdered ? $"{index++}." : "•", Classes = { "muted" }, MinWidth = 18 });
            var content = new StackPanel { Spacing = 5 };
            AddBlocks(content, item, depth + 1);
            row.Children.Add(PickerWindow.InColumn(content, 1));
            list.Children.Add(row);
        }
        return list;
    }

    private Control RenderTable(Table table, int depth)
    {
        var grid = new Grid();
        foreach (var row in table.OfType<TableRow>())
        {
            if (remainingBlocks <= 0)
                break;
            var rowIndex = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var column = 0;
            foreach (var cell in row.OfType<TableCell>().Take(20))
            {
                if (remainingBlocks-- <= 0)
                    break;
                if (grid.ColumnDefinitions.Count <= column)
                    grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 65 });
                var content = new StackPanel { Spacing = 4 };
                AddBlocks(content, cell, depth + 1);
                var border = new Border { Child = content, Padding = new Thickness(8, 5), BorderThickness = new Thickness(0, 0, 1, 1),
                    BorderBrush = App.Brush("Divider"), Background = row.IsHeader ? App.Brush("Raised") : null };
                grid.Children.Add(PickerWindow.InRow(PickerWindow.InColumn(border, column++), rowIndex));
            }
        }
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private SelectableTextBlock Text(ContainerInline? content, int size = 14, bool heading = false)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = Math.Ceiling(size * 1.55),
            FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal };
        if (content is not null)
            AddInlines(block.Inlines!, content);
        return block;
    }

    private void AddInlines(InlineCollection result, ContainerInline container, int depth = 0)
    {
        if (depth > 32)
            return;
        foreach (var inline in container)
        {
            if (remainingInlines-- <= 0)
                return;
            switch (inline)
            {
                case LiteralInline literal:
                    result.Add(new Run(literal.Content.ToString()));
                    break;
                case HtmlEntityInline entity:
                    result.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case CodeInline code:
                    result.Add(new Run(code.Content) { FontFamily = Mono, Background = App.Brush("Raised") });
                    break;
                case LineBreakInline { IsHard: true }:
                    result.Add(new LineBreak());
                    break;
                case LineBreakInline:
                    result.Add(new Run(" "));
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis, depth + 1);
                    result.Add(span);
                    break;
                case LinkInline { IsImage: true } link:
                    result.Add(new Run(InlineLabel(link)) { FontStyle = FontStyle.Italic });
                    break;
                case LinkInline link:
                    result.Add(Link(InlineLabel(link), link.Url));
                    break;
                case AutolinkInline link:
                    result.Add(Link(link.Url, link.IsEmail ? $"mailto:{link.Url}" : link.Url));
                    break;
                case ContainerInline nested:
                    AddInlines(result, nested, depth + 1);
                    break;
            }
        }
    }

    private Avalonia.Controls.Documents.Inline Link(string label, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
            return new Run(label);
        var button = new HyperlinkButton { Content = label, Padding = new Thickness(1, 0), MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14 };
        ToolTip.SetTip(button, url);
        button.Click += async (_, _) => await openLink(url);
        return new InlineUIContainer(button);
    }

    private static string InlineLabel(ContainerInline inline) => string.Concat(inline.Descendants().Select(child => child switch
    {
        LiteralInline literal => literal.Content.ToString(), CodeInline code => code.Content,
        HtmlEntityInline entity => entity.Transcoded.ToString(), _ => ""
    }));

    private static Border Box(Control child) => new() { Background = App.Brush("Raised"), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(13, 10), Child = child };
}
