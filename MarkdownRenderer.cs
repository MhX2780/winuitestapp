using System.Text.RegularExpressions;

namespace AdvaBrowser;

/// <summary>
/// Markdown-to-InlineUI renderer ported from UGA markdown_render.py.
/// Converts common Markdown constructs (headers, bold, italic, code blocks,
/// bullet lists, numbered lists, blockquotes, inline code) into WinUI Inline
/// / Block objects suitable for a RichTextBlock or TextBlock.
///
/// This is intentionally regex-based (like the Python original) covering
/// what LLM replies typically use.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// Converts raw Markdown text into a list of Inline objects
    /// suitable for adding to a RichTextBlock's Inlines collection.
    /// </summary>
    public static List<Microsoft.UI.Xaml.Documents.Inline> ParseInlines(string markdown)
    {
        var result = new List<Microsoft.UI.Xaml.Documents.Inline>();
        if (string.IsNullOrEmpty(markdown)) return result;

        // Split into lines to detect code blocks
        var lines = markdown.Split('\n');
        var inCodeBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Fenced code block detection
            var fenceMatch = Regex.Match(line.Trim(), @"^```(\w*)\s*$");
            if (fenceMatch.Success)
            {
                inCodeBlock = !inCodeBlock;
                if (inCodeBlock)
                {
                    // Add code block header
                    var lang = fenceMatch.Groups[1].Value;
                    var label = string.IsNullOrEmpty(lang) ? "" : $" {lang}";
                    result.Add(CreateRun($"┌─{label}", isCode: true));
                }
                else
                {
                    result.Add(CreateRun("└─", isCode: true));
                }
                result.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
                continue;
            }

            if (inCodeBlock)
            {
                result.Add(CreateRun($"│ {line}", isCode: true));
                result.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
                continue;
            }

            // Parse inline formatting
            ParseInlineLine(line, result);
            result.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        }

        return result;
    }

    /// <summary>
    /// Converts raw Markdown text to plain text with basic formatting indicators
    /// stripped, for use in simple TextBlock scenarios.
    /// </summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        var text = markdown;

        // Remove code fences
        text = Regex.Replace(text, @"```[\w]*\n?", "");
        text = Regex.Replace(text, @"```", "");

        // Remove bold markers
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__", "$1");

        // Remove italic markers
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"_(.+?)_", "$1");

        // Remove inline code markers
        text = Regex.Replace(text, @"`([^`]+)`", "$1");

        // Clean up header markers
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);

        // Clean up bullet markers
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

        // Remove code blocks entirely
        text = Regex.Replace(text, @"```[\s\S]*?```", "");

        // Remove inline formatting
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[-*+]\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\d+\.\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @">\s?", "", RegexOptions.Multiline);

        // Collapse whitespace
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    // ─── Private helpers ───

    private static void ParseInlineLine(string line, List<Microsoft.UI.Xaml.Documents.Inline> result)
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
            result.Add(run);
            return;
        }

        // Blockquote: > text
        if (trimmed.StartsWith(">"))
        {
            var content = trimmed.TrimStart('>').Trim();
            var run = new Microsoft.UI.Xaml.Documents.Run
            {
                Text = $"▏ {content}",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray)
            };
            result.Add(run);
            return;
        }

        // Bullet list: -, *, +
        var bulletMatch = Regex.Match(trimmed, @"^(\s*)[-*+]\s+(.*)$");
        if (bulletMatch.Success)
        {
            var indent = bulletMatch.Groups[1].Value;
            var content = bulletMatch.Groups[2].Value;
            result.Add(new Microsoft.UI.Xaml.Documents.Run { Text = indent + "  " });
            result.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "  ",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                // Bullet char
            });
            ParseInlineSpans(content, result);
            return;
        }

        // Numbered list: 1. text
        var numberedMatch = Regex.Match(trimmed, @"^(\s*)(\d+)\.\s+(.*)$");
        if (numberedMatch.Success)
        {
            var indent = numberedMatch.Groups[1].Value;
            var num = numberedMatch.Groups[2].Value;
            var content = numberedMatch.Groups[3].Value;
            result.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{indent}{num}. " });
            ParseInlineSpans(content, result);
            return;
        }

        // Regular line
        ParseInlineSpans(line, result);
    }

    private static void ParseInlineSpans(string text, List<Microsoft.UI.Xaml.Documents.Inline> result)
    {
        if (string.IsNullOrEmpty(text))
        {
            result.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "" });
            return;
        }

        // Split by inline patterns: **bold**, *italic*, `code`
        var pattern = @"(\*\*(.+?)\*\*|\*(.+?)\*|`([^`]+)`)";

        var matches = Regex.Matches(text, pattern);
        if (matches.Count == 0)
        {
            result.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
            return;
        }

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            // Add text before this match
            if (match.Index > lastIndex)
            {
                result.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = text[lastIndex..match.Index]
                });
            }

            if (match.Groups[2].Success) // **bold**
            {
                var run = new Microsoft.UI.Xaml.Documents.Run { Text = match.Groups[2].Value };
                run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                result.Add(run);
            }
            else if (match.Groups[3].Success) // *italic*
            {
                var run = new Microsoft.UI.Xaml.Documents.Run { Text = match.Groups[3].Value };
                run.FontStyle = Microsoft.UI.Text.FontStyle.Italic;
                result.Add(run);
            }
            else if (match.Groups[4].Success) // `code`
            {
                var run = new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = match.Groups[4].Value,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
                };
                result.Add(run);
            }

            lastIndex = match.Index + match.Length;
        }

        // Remaining text
        if (lastIndex < text.Length)
        {
            result.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = text[lastIndex..]
            });
        }
    }

    private static Microsoft.UI.Xaml.Documents.Run CreateRun(string text, bool isCode)
    {
        var run = new Microsoft.UI.Xaml.Documents.Run { Text = text };
        if (isCode)
        {
            run.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code");
            run.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Color.FromArgb(255, 100, 200, 255));
        }
        return run;
    }
}
