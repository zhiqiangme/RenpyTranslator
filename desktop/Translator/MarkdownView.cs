using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;

namespace RenpyTranslator;

internal static class MarkdownView
{
    // 解析为原生文档，不执行发布说明中的 HTML，也不自动加载外部图片。
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().UseEmphasisExtras().Build();
    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14, FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(32, 40, 52)),
            PagePadding = new Thickness(12), ColumnWidth = double.PositiveInfinity
        };
        Blocks(Markdown.Parse(markdown, Pipeline), document.Blocks);
        return document;
    }
    private static void Blocks(ContainerBlock source, BlockCollection target)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = new Paragraph { FontSize = Math.Max(15, 26 - heading.Level * 2), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 8) };
                    Inlines(heading.Inline, title.Inlines); target.Add(title); break;
                case ParagraphBlock paragraph:
                    var text = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                    Inlines(paragraph.Inline, text.Inlines); target.Add(text); break;
                case ListBlock list:
                    var items = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(24, 0, 0, 0) };
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) items.StartIndex = start;
                    foreach (var item in list.OfType<ListItemBlock>()) { var entry = new ListItem(); Blocks(item, entry.Blocks); items.ListItems.Add(entry); }
                    target.Add(items); break;
                case QuoteBlock quote:
                    var section = new Section { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 4, 0, 4), Margin = new Thickness(0, 4, 0, 10) };
                    Blocks(quote, section.Blocks); target.Add(section); break;
                case CodeBlock code:
                    target.Add(new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Consolas"), FontSize = 13, Background = Brushes.WhiteSmoke, Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 12) }); break;
                case ThematicBreakBlock:
                    target.Add(new Paragraph { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 8) }); break;
                case MdTable table:
                    var grid = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
                    var rows = new TableRowGroup(); grid.RowGroups.Add(rows);
                    foreach (var row in table.OfType<MdRow>())
                    {
                        var output = new TableRow(); rows.Rows.Add(output);
                        foreach (var cell in row.OfType<MdCell>())
                        {
                            var content = new TableCell { Padding = new Thickness(6), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5), FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal };
                            Blocks(cell, content.Blocks); output.Cells.Add(content);
                        }
                    }
                    target.Add(grid); break;
                case ContainerBlock container:
                    Blocks(container, target); break;
            }
        }
    }
    private static void Inlines(ContainerInline? source, InlineCollection target)
    {
        if (source is null) return;
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = Brushes.WhiteSmoke }); break;
                case LineBreakInline line: target.Add(line.IsHard ? new LineBreak() : new Run(" ")); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else { if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold; if (emphasis.DelimiterCount % 2 == 1) span.FontStyle = FontStyles.Italic; }
                    Inlines(emphasis, span.Inlines); target.Add(span); break;
                case LinkInline link:
                    if (!link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri };
                        Inlines(link, hyperlink.Inlines);
                        hyperlink.RequestNavigate += (_, args) =>
                        {
                            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                            catch (Exception) { MessageBox.Show("无法打开链接，请检查系统默认浏览器设置。"); }
                            args.Handled = true;
                        };
                        target.Add(hyperlink);
                    }
                    else Inlines(link, target);
                    break;
                case AutolinkInline auto: target.Add(new Run(auto.Url)); break;
                case ContainerInline container: Inlines(container, target); break;
            }
        }
    }
}
