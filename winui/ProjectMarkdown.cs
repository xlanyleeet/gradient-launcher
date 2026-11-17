using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gradient;

internal static class ProjectMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static StackPanel Render(string markdown, Uri? projectUrl)
    {
        var panel = new StackPanel { Spacing = 11, MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Left };
        AddBlocks(panel, Markdown.Parse(markdown, Pipeline), projectUrl);
        return panel;
    }

    private static void AddBlocks(Panel target, ContainerBlock blocks, Uri? baseUri)
    {
        var listNumber = blocks is ListBlock list && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ListItemBlock item:
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.Children.Add(new TextBlock { Text = blocks is ListBlock { IsOrdered: true } ? $"{listNumber++}." : "•" });
                    var body = new StackPanel { Spacing = 5 };
                    AddBlocks(body, item, baseUri);
                    Grid.SetColumn(body, 1);
                    row.Children.Add(body);
                    target.Children.Add(row);
                    break;
                case QuoteBlock quote:
                    var inset = new StackPanel { Spacing = 8, Margin = new Thickness(18, 0, 0, 0) };
                    AddBlocks(inset, quote, baseUri);
                    target.Children.Add(inset);
                    break;
                case ContainerBlock container:
                    var group = new StackPanel { Spacing = 5 };
                    AddBlocks(group, container, baseUri);
                    target.Children.Add(group);
                    break;
                case LeafBlock leaf:
                    var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                    var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
                    if (leaf is HeadingBlock heading)
                    {
                        paragraph.FontSize = heading.Level < 3 ? 22 : 17;
                        paragraph.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    }
                    if (leaf is CodeBlock code)
                        paragraph.Inlines.Add(new Run { Text = code.Lines.ToString(), FontFamily = new FontFamily("Consolas") });
                    else
                        Append(paragraph.Inlines, leaf.Inline, baseUri);
                    if (paragraph.Inlines.Count > 0)
                    {
                        text.Blocks.Add(paragraph);
                        target.Children.Add(text);
                    }
                    break;
            }
        }
    }

    private static void Append(InlineCollection output, ContainerInline? content, Uri? baseUri)
    {
        if (content is null) return;
        foreach (var inline in content)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    output.Add(new Run { Text = literal.Content.ToString() });
                    break;
                case LineBreakInline line:
                    if (line.IsHard) output.Add(new LineBreak());
                    else output.Add(new Run { Text = " " });
                    break;
                case CodeInline code:
                    output.Add(new Run { Text = code.Content, FontFamily = new FontFamily("Consolas") });
                    break;
                case HtmlEntityInline entity:
                    output.Add(new Run { Text = entity.Transcoded.ToString() });
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    Append(span.Inlines, emphasis, baseUri);
                    output.Add(span);
                    break;
                case LinkInline { IsImage: true } image:
                    AddImage(output, image, null, baseUri);
                    break;
                case LinkInline link:
                    var address = Https(link.Url, baseUri);
                    if (link.FirstChild is LinkInline { IsImage: true } linkedImage && linkedImage.NextSibling is null)
                        AddImage(output, linkedImage, address, baseUri);
                    else if (address is not null)
                    {
                        var hyperlink = new Hyperlink { NavigateUri = address };
                        hyperlink.Inlines.Add(new Run { Text = PlainText(link) });
                        output.Add(hyperlink);
                    }
                    else Append(output, link, baseUri);
                    break;
                case AutolinkInline autoLink when Https(autoLink.Url, baseUri) is { } autoAddress:
                    var auto = new Hyperlink { NavigateUri = autoAddress };
                    auto.Inlines.Add(new Run { Text = autoLink.Url });
                    output.Add(auto);
                    break;
            }
        }
    }

    private static void AddImage(InlineCollection target, LinkInline image, Uri? destination, Uri? baseUri)
    {
        var label = PlainText(image);
        if (Https(image.Url, baseUri) is not { } url)
        {
            target.Add(new Run { Text = label });
            return;
        }
        var illustration = new Image
        {
            Source = new BitmapImage(url) { DecodePixelWidth = 620 },
            MaxWidth = 420, MaxHeight = 220, Stretch = Stretch.Uniform,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(illustration, label);
        target.Add(new InlineUIContainer
        {
            Child = destination is null ? illustration :
                new HyperlinkButton { Content = illustration, NavigateUri = destination, Padding = new Thickness(0) }
        });
    }

    private static string PlainText(ContainerInline content) => string.Concat(content.Select(inline => inline switch
    {
        LiteralInline text => text.Content.ToString(),
        CodeInline code => code.Content,
        HtmlEntityInline entity => entity.Transcoded.ToString(),
        ContainerInline nested => PlainText(nested),
        _ => "",
    }));

    private static Uri? Https(string? target, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var valid = Uri.TryCreate(target, UriKind.Absolute, out var url) ||
            (baseUri is not null && Uri.TryCreate(baseUri, target, out url));
        return valid && url?.Scheme == Uri.UriSchemeHttps ? url : null;
    }
}
