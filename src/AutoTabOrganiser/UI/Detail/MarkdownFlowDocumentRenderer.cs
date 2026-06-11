using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Markdig;
using Markdig.Syntax;
using MdBlock = Markdig.Syntax.Block;
using MdInlines = Markdig.Syntax.Inlines;

namespace AutoTabOrganiser.UI.Detail
{
    /// <summary>
    /// Markdig AST -> WPF FlowDocument renderer. Covers the subset used in tab
    /// descriptions: headings, paragraphs, emphasis, inline + fenced code,
    /// ordered/bullet lists, block quotes, thematic breaks, links, autolinks,
    /// and line breaks. Anything else falls back to its raw text so nothing
    /// is silently dropped.
    /// </summary>
    internal static class MarkdownFlowDocumentRenderer
    {
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        private static readonly FontFamily MonospaceFont =
            new FontFamily("Consolas, Cascadia Mono, Courier New");

        private static readonly Brush CodeBackground = Frozen(Color.FromArgb(40, 128, 128, 128));
        private static readonly Brush RuleBrush = Frozen(Color.FromArgb(80, 128, 128, 128));

        public static FlowDocument Render(string markdown)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };
            if (string.IsNullOrEmpty(markdown)) return doc;

            var ast = Markdown.Parse(markdown, Pipeline);
            foreach (var block in ast)
                AppendBlock(doc.Blocks, block);
            return doc;
        }

        private static void AppendBlock(BlockCollection target, MdBlock block)
        {
            switch (block)
            {
                case HeadingBlock h:
                {
                    var p = new Paragraph
                    {
                        FontSize = HeadingSize(h.Level),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 8, 0, 4)
                    };
                    AppendInlines(p.Inlines, h.Inline);
                    target.Add(p);
                    break;
                }
                case ParagraphBlock pb:
                {
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                    AppendInlines(p.Inlines, pb.Inline);
                    target.Add(p);
                    break;
                }
                case QuoteBlock qb:
                {
                    var sec = new Section
                    {
                        Margin = new Thickness(0, 0, 0, 6),
                        BorderBrush = RuleBrush,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(10, 2, 0, 2)
                    };
                    foreach (var child in qb)
                        AppendBlock(sec.Blocks, child);
                    target.Add(sec);
                    break;
                }
                case ListBlock lb:
                {
                    var list = new List
                    {
                        MarkerStyle = lb.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 6),
                        Padding = new Thickness(24, 0, 0, 0)
                    };
                    foreach (var child in lb)
                    {
                        if (child is ListItemBlock li)
                        {
                            var item = new ListItem();
                            foreach (var sub in li)
                                AppendBlock(item.Blocks, sub);
                            list.ListItems.Add(item);
                        }
                    }
                    target.Add(list);
                    break;
                }
                case CodeBlock cb: // FencedCodeBlock inherits CodeBlock
                {
                    var p = new Paragraph
                    {
                        FontFamily = MonospaceFont,
                        Background = CodeBackground,
                        Margin = new Thickness(0, 4, 0, 8),
                        Padding = new Thickness(8, 4, 8, 4),
                        TextIndent = 0
                    };
                    // Iterate cb.Lines.Lines explicitly. The StringLineGroup's ToString varies
                    // by Markdig version (some return a "1-5" line-range summary instead of the
                    // joined text) — explicit slicing guarantees we render the actual code.
                    p.Inlines.Add(new Run(JoinLines(cb.Lines)));
                    target.Add(p);
                    break;
                }
                case ThematicBreakBlock _:
                {
                    var rect = new Rectangle
                    {
                        Height = 1,
                        Margin = new Thickness(0, 4, 0, 8),
                        Fill = RuleBrush
                    };
                    target.Add(new BlockUIContainer(rect));
                    break;
                }
                default:
                {
                    if (block is LeafBlock leaf && leaf.Lines.Count > 0)
                    {
                        target.Add(new Paragraph(new Run(JoinLines(leaf.Lines)))
                        { Margin = new Thickness(0, 0, 0, 6) });
                    }
                    else if (block is ContainerBlock container)
                    {
                        // Recurse so unknown wrappers (e.g. tables) at least surface their children.
                        foreach (var child in container)
                            AppendBlock(target, child);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Joins a Markdig <c>StringLineGroup</c>'s lines with newlines, slicing each line by
        /// its declared range so we get the actual source text — not <c>ToString()</c>, which
        /// in some Markdig versions returns a "(N-M)" line-range summary rather than the body.
        /// </summary>
        private static string JoinLines(Markdig.Helpers.StringLineGroup group)
        {
            if (group.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            var lines = group.Lines; // backing array; only the first .Count entries are valid
            for (int i = 0; i < group.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(lines[i].Slice.ToString());
            }
            return sb.ToString();
        }

        private static double HeadingSize(int level)
        {
            switch (level)
            {
                case 1: return 18;
                case 2: return 15;
                case 3: return 13;
                case 4: return 12.5;
                default: return 12;
            }
        }

        private static void AppendInlines(InlineCollection target, MdInlines.ContainerInline container)
        {
            if (container == null) return;
            foreach (var inline in container)
                AppendInline(target, inline);
        }

        private static void AppendInline(InlineCollection target, MdInlines.Inline inline)
        {
            switch (inline)
            {
                case MdInlines.LiteralInline lit:
                    target.Add(new Run(lit.Content.ToString()));
                    break;
                case MdInlines.EmphasisInline em:
                {
                    Span span = em.DelimiterCount >= 2 ? (Span)new Bold() : new Italic();
                    foreach (var child in em)
                        AppendInline(span.Inlines, child);
                    target.Add(span);
                    break;
                }
                case MdInlines.CodeInline ci:
                    target.Add(new Run(ci.Content)
                    {
                        FontFamily = MonospaceFont,
                        Background = CodeBackground
                    });
                    break;
                case MdInlines.LinkInline link when !link.IsImage:
                {
                    var hyperlink = new Hyperlink();
                    if (!string.IsNullOrEmpty(link.Url) &&
                        Uri.TryCreate(link.Url, UriKind.RelativeOrAbsolute, out var uri))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += OnLinkRequestNavigate;
                    }
                    foreach (var child in link)
                        AppendInline(hyperlink.Inlines, child);
                    target.Add(hyperlink);
                    break;
                }
                case MdInlines.LinkInline image when image.IsImage:
                {
                    var alt = string.Empty;
                    foreach (var child in image)
                        if (child is MdInlines.LiteralInline lit) alt += lit.Content.ToString();
                    target.Add(new Run("[image" + (alt.Length > 0 ? ": " + alt : "") + "]")
                    { FontStyle = FontStyles.Italic });
                    break;
                }
                case MdInlines.AutolinkInline al:
                {
                    var hyperlink = new Hyperlink(new Run(al.Url));
                    if (Uri.TryCreate(al.Url, UriKind.RelativeOrAbsolute, out var uri))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += OnLinkRequestNavigate;
                    }
                    target.Add(hyperlink);
                    break;
                }
                case MdInlines.LineBreakInline lb:
                    // CommonMark soft breaks render as a space; hard breaks as an explicit break.
                    if (lb.IsHard) target.Add(new LineBreak());
                    else target.Add(new Run(" "));
                    break;
                case MdInlines.HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
                case MdInlines.ContainerInline container:
                    foreach (var child in container)
                        AppendInline(target, child);
                    break;
                default:
                    target.Add(new Run(inline?.ToString() ?? string.Empty));
                    break;
            }
        }

        private static void OnLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                // Descriptions can arrive in .sql files from anyone; UseShellExecute on an
                // arbitrary scheme would invoke whatever handler the URI names (ms-msdt:,
                // file:, …). Only ever hand http(s) to the browser.
                var uri = e?.Uri;
                e.Handled = true; // even when blocked — never let WPF navigate the FlowDocument
                if (uri == null || !uri.IsAbsoluteUri) return;
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Opening a link should never crash the tool window.
            }
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
