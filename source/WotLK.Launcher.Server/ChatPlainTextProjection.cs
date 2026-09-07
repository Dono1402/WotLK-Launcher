using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace WotLK.Launcher.Server;

/// <summary>Projects the parsed Markdown tree to the existing plain-text game contract.</summary>
internal static class ChatPlainTextProjection
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        MarkdownPipelineBuilder builder = new MarkdownPipelineBuilder()
            .DisableHtml().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);
        builder.InlineParsers.OfType<EmphasisInlineParser>().Single()
            .EmphasisDescriptors.Add(new EmphasisDescriptor('|', 2, 2, true));
        return builder.Build();
    }

    internal static string ForLegacy(string markdown)
    {
        StringBuilder text = new();
        WriteBlocks(Markdown.Parse(markdown, Pipeline), text);
        string plain = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (plain.Length == 0) plain = "[Message sans texte]";
        // Repeated reference links can expand beyond the original Markdown length.
        // Keep the established game limit without cutting a Unicode surrogate pair.
        if (plain.Length > ChatMessageValidation.MaximumCharacters)
        {
            int length = ChatMessageValidation.MaximumCharacters - 1;
            if (char.IsHighSurrogate(plain[length - 1])) length--;
            plain = plain[..length] + "…";
        }
        return ChatMessageValidation.Normalize(plain);
    }

    private static void WriteBlocks(ContainerBlock blocks, StringBuilder text)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case ListItemBlock item:
                    text.Append("- ");
                    WriteBlocks(item, text);
                    break;
                case QuoteBlock quote:
                    text.Append("> ");
                    WriteBlocks(quote, text);
                    break;
                case CodeBlock code:
                    text.Append(code.Lines.ToString());
                    break;
                case ContainerBlock container:
                    WriteBlocks(container, text);
                    break;
                case LeafBlock leaf when leaf.Inline is not null:
                    WriteInlines(leaf.Inline, text);
                    break;
                case ThematicBreakBlock:
                    text.Append('—');
                    break;
            }
            if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
        }
    }

    private static void WriteInlines(ContainerInline container, StringBuilder text)
    {
        for (Inline? node = container.FirstChild; node is not null; node = node.NextSibling)
        {
            switch (node)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case HtmlEntityInline entity: text.Append(entity.Transcoded.ToString()); break;
                case HtmlInline html: text.Append(html.Tag); break;
                case AutolinkInline link: text.Append(link.Url); break;
                case LineBreakInline: text.Append('\n'); break;
                case LinkInline link:
                    StringBuilder label = new();
                    WriteInlines(link, label);
                    text.Append(label);
                    string? url = link.Url;
                    if (!string.IsNullOrEmpty(url) && label.ToString() != url)
                    {
                        if (label.Length > 0) text.Append(" (").Append(url).Append(')');
                        else text.Append(url);
                    }
                    break;
                case ContainerInline nested: WriteInlines(nested, text); break;
            }
        }
    }
}
