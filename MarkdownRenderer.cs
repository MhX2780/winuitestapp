using System.Text.RegularExpressions;

namespace AdvaBrowser;

/// <summary>
/// Markdown renderer with code block Cards, syntax highlighting, and copy buttons.
/// Returns a list of Block elements (Paragraph + InlineUIContainer) for RichTextBlock.
/// Code blocks are rendered as Border cards with colored syntax and a copy button.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// Parses markdown into Blocks for a RichTextBlock.
    /// Code blocks become InlineUIContainer cards with syntax highlighting and copy button.
    /// </summary>
    public static List<Microsoft.UI.Xaml.Documents.Block> ParseBlocks(string markdown)
    {
        var blocks = new List<Microsoft.UI.Xaml.Documents.Block>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        var lines = markdown.Split('\n');
        var inCodeBlock = false;
        var codeLines = new List<string>();
        var codeLang = "";
        var currentParagraph = new Microsoft.UI.Xaml.Documents.Paragraph();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Fenced code block detection
            var fenceMatch = Regex.Match(line.Trim(), @"^```(\w*)\s*$");
            if (fenceMatch.Success)
            {
                // Flush any accumulated text as a paragraph
                if (currentParagraph.Inlines.Count > 0)
                {
                    blocks.Add(currentParagraph);
                    currentParagraph = new Microsoft.UI.Xaml.Documents.Paragraph();
                }

                inCodeBlock = !inCodeBlock;
                if (inCodeBlock)
                {
                    codeLang = fenceMatch.Groups[1].Value;
                    codeLines = new List<string>();
                }
                else
                {
                    // End of code block — create card
                    var codeContent = string.Join("\n", codeLines);
                    if (!string.IsNullOrEmpty(codeContent))
                    {
                        blocks.Add(CreateCodeCard(codeLang, codeContent));
                    }
                    codeLines = new List<string>();
                    codeLang = "";
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            // Empty line = paragraph break
            if (string.IsNullOrWhiteSpace(line) && currentParagraph.Inlines.Count > 0)
            {
                blocks.Add(currentParagraph);
                currentParagraph = new Microsoft.UI.Xaml.Documents.Paragraph();
                continue;
            }

            // Horizontal rule: ---, ***, ___ (must be on its own line, at least 3 chars)
            var hrMatch = Regex.Match(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})\s*$");
            if (hrMatch.Success)
            {
                // Flush any accumulated text
                if (currentParagraph.Inlines.Count > 0)
                {
                    blocks.Add(currentParagraph);
                    currentParagraph = new Microsoft.UI.Xaml.Documents.Paragraph();
                }
                blocks.Add(CreateHorizontalRule());
                continue;
            }

            // Parse inline formatting into current paragraph
            ParseInlineLine(line, currentParagraph.Inlines);
            currentParagraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        }

        // Flush last paragraph
        if (currentParagraph.Inlines.Count > 0)
            blocks.Add(currentParagraph);

        return blocks;
    }

    /// <summary>
    /// Legacy: Converts raw Markdown text into a list of Inline objects
    /// for backward compatibility.
    /// </summary>
    public static List<Microsoft.UI.Xaml.Documents.Inline> ParseInlines(string markdown)
    {
        var blocks = ParseBlocks(markdown);
        var inlines = new List<Microsoft.UI.Xaml.Documents.Inline>();
        foreach (var block in blocks)
        {
            if (block is Microsoft.UI.Xaml.Documents.Paragraph para)
            {
                foreach (var inline in para.Inlines)
                    inlines.Add(inline);
                inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            }
            else if (block is Microsoft.UI.Xaml.Documents.Paragraph p)
            {
                // InlineUIContainer blocks — add as-is
                foreach (var inline in p.Inlines)
                    inlines.Add(inline);
                inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            }
        }
        return inlines;
    }

    /// <summary>
    /// Extracts all code blocks from markdown as (language, code) tuples.
    /// Used by the Artifacts page.
    /// </summary>
    public static List<(string Language, string Code, string Preview)> ExtractCodeBlocks(string markdown)
    {
        var results = new List<(string, string, string)>();
        if (string.IsNullOrEmpty(markdown)) return results;

        var matches = Regex.Matches(markdown, @"```(\w*)\n([\s\S]*?)```");
        foreach (Match m in matches)
        {
            var lang = m.Groups[1].Value;
            var code = m.Groups[2].Value.TrimEnd('\n', '\r');
            var firstLine = code.Split('\n').FirstOrDefault()?.Trim() ?? "";
            if (firstLine.Length > 80) firstLine = firstLine[..80] + "...";
            results.Add((lang, code, firstLine));
        }
        return results;
    }

    // ─── Horizontal Rule ───

    private static Microsoft.UI.Xaml.Documents.Paragraph CreateHorizontalRule()
    {
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();

        var border = new Microsoft.UI.Xaml.Controls.Border
        {
            Height = 1,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 80, 80, 80)),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 8),
        };

        var container = new Microsoft.UI.Xaml.Documents.InlineUIContainer { Child = border };
        paragraph.Inlines.Add(container);
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());

        return paragraph;
    }

    // ─── Code Card with Syntax Highlighting ───

    private static Microsoft.UI.Xaml.Documents.Paragraph CreateCodeCard(string language, string code)
    {
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();

        // Create the card UI
        var card = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 30, 30, 30)),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
            Padding = new Microsoft.UI.Xaml.Thickness(0),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            MaxWidth = 650,
        };

        var stack = new Microsoft.UI.Xaml.Controls.StackPanel();

        // Header: language label + copy button
        var headerPanel = new Microsoft.UI.Xaml.Controls.Grid
        {
            Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 45, 45, 45)),
        };
        headerPanel.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        headerPanel.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });

        var langText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = string.IsNullOrEmpty(language) ? "code" : language,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 160, 160, 160)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(langText, 0);
        headerPanel.Children.Add(langText);

        var copyBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8C8", FontSize = 14 },
            FontSize = 12,
            Padding = new Microsoft.UI.Xaml.Thickness(6, 2, 6, 2),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 200, 200, 200)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(copyBtn, "Copy code");
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(copyBtn, 1);
        copyBtn.Click += (s, e) =>
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        };
        headerPanel.Children.Add(copyBtn);

        stack.Children.Add(headerPanel);

        // Code content with syntax highlighting via RichEditBox
        var codeBox = new Microsoft.UI.Xaml.Controls.RichEditBox
        {
            IsReadOnly = true,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
            FontSize = 13,
            Padding = new Microsoft.UI.Xaml.Thickness(12, 8, 12, 10),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 25, 25, 25)),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 212, 212, 212)),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            MinHeight = 40,
        };

        codeBox.TextDocument.SetText(Microsoft.UI.Text.TextSetOptions.None, code);
        SyntaxHighlighter.ApplyHighlighting(codeBox, language);
        stack.Children.Add(codeBox);

        card.Child = stack;

        // Wrap in InlineUIContainer
        var container = new Microsoft.UI.Xaml.Documents.InlineUIContainer { Child = card };
        paragraph.Inlines.Add(container);
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());

        return paragraph;
    }

    // Syntax highlighting is now handled by SyntaxHighlighter.cs using RichEditBox.

    // ─── Inline Parsing ───

    private static void ParseInlineLine(string line, Microsoft.UI.Xaml.Documents.InlineCollection inlines)
    {
        var trimmed = line.TrimStart();

        // Headers: #, ##, ###
        var headerMatch = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
        if (headerMatch.Success)
        {
            var content = headerMatch.Groups[2].Value;
            var weight = headerMatch.Groups[1].Value.Length switch
            {
                1 => Microsoft.UI.Text.FontWeights.Bold,
                2 => Microsoft.UI.Text.FontWeights.SemiBold,
                _ => Microsoft.UI.Text.FontWeights.Bold,
            };
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = content };
            run.FontWeight = weight;
            inlines.Add(run);
            return;
        }

        // Blockquote: > text
        if (trimmed.StartsWith(">"))
        {
            var content = trimmed.TrimStart('>').Trim();
            var run = new Microsoft.UI.Xaml.Documents.Run
            {
                Text = $"  {content}",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray)
            };
            inlines.Add(run);
            return;
        }

        // Bullet list: -, *, +
        var bulletMatch = Regex.Match(trimmed, @"^(\s*)[-*+]\s+(.*)$");
        if (bulletMatch.Success)
        {
            var indent = bulletMatch.Groups[1].Value;
            var content = bulletMatch.Groups[2].Value;
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = indent + "  \u2022 " });
            ParseInlineSpans(content, inlines);
            return;
        }

        // Numbered list: 1. text
        var numberedMatch = Regex.Match(trimmed, @"^(\s*)(\d+)\.\s+(.*)$");
        if (numberedMatch.Success)
        {
            var indent = numberedMatch.Groups[1].Value;
            var num = numberedMatch.Groups[2].Value;
            var content = numberedMatch.Groups[3].Value;
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{indent}{num}. " });
            ParseInlineSpans(content, inlines);
            return;
        }

        // Regular line
        ParseInlineSpans(line, inlines);
    }

    private static void ParseInlineSpans(string text, Microsoft.UI.Xaml.Documents.InlineCollection inlines)
    {
        if (string.IsNullOrEmpty(text))
        {
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "" });
            return;
        }

        // Split by inline patterns: **bold**, *italic*, `code`
        var pattern = @"(\*\*(.+?)\*\*|\*(.+?)\*|`([^`]+)`)";

        var matches = Regex.Matches(text, pattern);
        if (matches.Count == 0)
        {
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
            return;
        }

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = text[lastIndex..match.Index]
                });
            }

            if (match.Groups[2].Success) // **bold**
            {
                var run = new Microsoft.UI.Xaml.Documents.Run { Text = match.Groups[2].Value };
                run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                inlines.Add(run);
            }
            else if (match.Groups[3].Success) // *italic*
            {
                var run = new Microsoft.UI.Xaml.Documents.Run { Text = match.Groups[3].Value };
                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                inlines.Add(run);
            }
            else if (match.Groups[4].Success) // `code`
            {
                var run = new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = match.Groups[4].Value,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
                };
                inlines.Add(run);
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = text[lastIndex..]
            });
        }
    }

    /// <summary>
    /// Converts raw Markdown text to plain text with basic formatting stripped.
    /// </summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var text = markdown;
        text = Regex.Replace(text, @"```[\w]*\n?", "");
        text = Regex.Replace(text, @"```", "");
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"_(.+?)_", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^(\s*)[-*+]\s+", "$1- ", RegexOptions.Multiline);
        return text.Trim();
    }

    /// <summary>
    /// Strips all Markdown and returns pure text content.
    /// </summary>
    public static string StripMarkdown(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var text = markdown;
        text = Regex.Replace(text, @"```[\s\S]*?```", "");
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[-*+]\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\d+\.\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @">\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
