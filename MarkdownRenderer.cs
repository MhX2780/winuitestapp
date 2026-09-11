using System.Text.RegularExpressions;

namespace UGA;

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

            // ─── Markdown table detection ───
            var tableLine = line.Trim();
            if (tableLine.StartsWith("|") && tableLine.EndsWith("|"))
            {
                // Flush paragraph before table
                if (currentParagraph.Inlines.Count > 0)
                {
                    blocks.Add(currentParagraph);
                    currentParagraph = new Microsoft.UI.Xaml.Documents.Paragraph();
                }

                var tableRows = new List<List<string>>();
                while (i < lines.Length)
                {
                    var tLine = lines[i].Trim();
                    if (!tLine.StartsWith("|") || !tLine.EndsWith("|")) break;
                    var cells = tLine.Trim('|').Split('|');
                    var row = cells.Select(c => c.Trim()).ToList();
                    tableRows.Add(row);
                    i++;
                }
                // If we consumed every remaining line, the table might still be
                // streaming in (no following non-table line has arrived yet to
                // confirm it's done) — show a "Creating Table" placeholder instead
                // of a clickable card for a table that could still grow.
                bool tableStillStreaming = i >= lines.Length;
                i--; // step back for the outer loop

                // Skip separator row (|---|---|)
                var dataRows = new List<List<string>>();
                bool skippedSep = false;
                foreach (var r in tableRows)
                {
                    if (!skippedSep && r.All(c => Regex.IsMatch(c, @"^:?-+:?$")))
                    {
                        skippedSep = true;
                        continue;
                    }
                    dataRows.Add(r);
                }

                if (dataRows.Count > 0)
                {
                    blocks.Add(tableStillStreaming
                        ? CreatePendingTableCard()
                        : CreateTableCard(dataRows));
                }
                continue;
            }

            // Parse inline formatting into current paragraph
            ParseInlineLine(line, currentParagraph.Inlines);
            currentParagraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        }

        // Flush last paragraph
        if (currentParagraph.Inlines.Count > 0)
            blocks.Add(currentParagraph);

        // Code block still open at end of text (streaming in progress) —
        // show a placeholder card instead of hiding the block until the
        // closing fence arrives.
        if (inCodeBlock)
            blocks.Add(CreatePendingCodeCard(codeLang));

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

        // Line-by-line fence detection (mirrors ParseBlocks) instead of a single
        // regex — the old regex required a "\n" immediately after the opening
        // fence/language tag, so single-line code blocks or blocks missing a
        // closing fence (e.g. cut off mid-stream) were silently skipped, causing
        // code visible in the chat to not appear on the Artifacts page at all.
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCodeBlock = false;
        var codeLines = new List<string>();
        var codeLang = "";

        void Flush()
        {
            var code = string.Join("\n", codeLines).TrimEnd('\n', '\r');
            if (!string.IsNullOrEmpty(code))
            {
                var firstLine = code.Split('\n').FirstOrDefault()?.Trim() ?? "";
                if (firstLine.Length > 80) firstLine = firstLine[..80] + "...";
                results.Add((codeLang, code, firstLine));
            }
            codeLines = new List<string>();
            codeLang = "";
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            var fenceMatch = Regex.Match(line.Trim(), @"^```(\w*)\s*$");
            if (fenceMatch.Success)
            {
                if (inCodeBlock)
                {
                    // Closing fence
                    Flush();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                    codeLang = fenceMatch.Groups[1].Value;
                }
                continue;
            }

            if (inCodeBlock)
                codeLines.Add(line);
        }

        // Unterminated block (e.g. cut off mid-stream) — still show what we have.
        if (inCodeBlock)
            Flush();

        return results;
    }

    /// <summary>
    /// Extracts all markdown pipe-tables from text as row lists (first row =
    /// header). Mirrors the table detection in ParseBlocks. Used by TablesPage.
    /// A table still being streamed (no line after it yet to confirm it's
    /// finished) is intentionally skipped, matching the "Creating Table"
    /// placeholder behavior in chat — an in-progress table shouldn't show up
    /// as a finished entry in the Tables page.
    /// </summary>
    public static List<List<List<string>>> ExtractTables(string markdown)
    {
        var results = new List<List<List<string>>>();
        if (string.IsNullOrEmpty(markdown)) return results;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var tableLine = lines[i].Trim();
            if (tableLine.StartsWith("|") && tableLine.EndsWith("|"))
            {
                var tableRows = new List<List<string>>();
                while (i < lines.Length)
                {
                    var tLine = lines[i].Trim();
                    if (!tLine.StartsWith("|") || !tLine.EndsWith("|")) break;
                    var cells = tLine.Trim('|').Split('|');
                    tableRows.Add(cells.Select(c => c.Trim()).ToList());
                    i++;
                }
                bool tableStillStreaming = i >= lines.Length;

                var dataRows = new List<List<string>>();
                bool skippedSep = false;
                foreach (var r in tableRows)
                {
                    if (!skippedSep && r.All(c => Regex.IsMatch(c, @"^:?-+:?$")))
                    {
                        skippedSep = true;
                        continue;
                    }
                    dataRows.Add(r);
                }

                if (dataRows.Count > 0 && !tableStillStreaming)
                    results.Add(dataRows);

                continue; // i already advanced past the table
            }
            i++;
        }

        return results;
    }

    // ─── Markdown Table ───

    /// <summary>
    /// Compact clickable summary card for a completed table (icon + row/column
    /// count). Tapping it opens a dialog with the full table and export options
    /// (copy as Markdown / save as .xlsx), instead of rendering potentially wide
    /// tables directly inline in the narrow chat column.
    /// </summary>
    private static Microsoft.UI.Xaml.Documents.Paragraph CreateTableCard(List<List<string>> dataRows)
    {
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
        var numCols = dataRows.Max(r => r.Count);
        var numDataRows = Math.Max(0, dataRows.Count - 1); // exclude header

        var card = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 30, 30, 30)),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
            Padding = new Microsoft.UI.Xaml.Thickness(14, 12, 14, 12),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            MaxWidth = 360,
        };

        var row = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 10,
        };

        var icon = new Microsoft.UI.Xaml.Controls.FontIcon
        {
            Glyph = "\uE8EF", // ViewAll / grid-like glyph (Segoe Fluent Icons)
            FontSize = 20,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 160, 160, 160)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        row.Children.Add(icon);

        var textStack = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 2 };
        textStack.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "Table",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 220, 220, 220)),
        });
        textStack.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = $"{numDataRows} row{(numDataRows == 1 ? "" : "s")} · {numCols} column{(numCols == 1 ? "" : "s")}",
            FontSize = 11,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 150, 150, 150)),
        });
        row.Children.Add(textStack);

        var expandIcon = new Microsoft.UI.Xaml.Controls.FontIcon
        {
            Glyph = "\uE8A7", // chevron right
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 130, 130, 130)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Margin = new Microsoft.UI.Xaml.Thickness(20, 0, 0, 0),
        };
        row.Children.Add(expandIcon);

        card.Child = row;
        card.Tapped += (s, e) => ShowTableDialog(card.XamlRoot, dataRows);
        CursorHelper.SetHandOn(card); // hand cursor on hover — signals it's clickable

        var container = new Microsoft.UI.Xaml.Documents.InlineUIContainer { Child = card };
        paragraph.Inlines.Add(container);
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        return paragraph;
    }

    /// <summary>
    /// Placeholder shown while a table is still streaming in — mirrors
    /// CreatePendingCodeCard's look (ring progress + status text) so a table
    /// doesn't render half-finished/jittering while more rows keep arriving.
    /// </summary>
    private static Microsoft.UI.Xaml.Documents.Paragraph CreatePendingTableCard()
    {
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();

        var card = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 30, 30, 30)),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
            Padding = new Microsoft.UI.Xaml.Thickness(14, 12, 14, 12),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            MaxWidth = 360,
        };

        var row = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 10,
        };

        var ring = new Microsoft.UI.Xaml.Controls.ProgressRing
        {
            IsActive = true,
            Width = 18,
            Height = 18,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 160, 160, 160)),
        };
        row.Children.Add(ring);

        var statusText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "Creating Table",
            FontSize = 13,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 200, 200, 200)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        row.Children.Add(statusText);

        card.Child = row;
        var container = new Microsoft.UI.Xaml.Documents.InlineUIContainer { Child = card };
        paragraph.Inlines.Add(container);
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        return paragraph;
    }

    /// <summary>
    /// Full-table dialog opened by tapping a table card: scrollable grid of
    /// the actual data, plus "Copy as Markdown" and "Export as .xlsx" actions.
    /// Public so TablesPage can reuse it for its own "View" button.
    /// </summary>
    public static async void ShowTableDialog(Microsoft.UI.Xaml.XamlRoot? xamlRoot, List<List<string>> dataRows)
    {
        if (xamlRoot == null) return;

        var numCols = dataRows.Max(r => r.Count);
        var scrollViewer = new Microsoft.UI.Xaml.Controls.ScrollViewer
        {
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
            MaxHeight = 420,
        };

        var grid = new Microsoft.UI.Xaml.Controls.Grid();
        for (int c = 0; c < numCols; c++)
            grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        for (int r = 0; r < dataRows.Count; r++)
            grid.RowDefinitions.Add(new Microsoft.UI.Xaml.Controls.RowDefinition { Height = Microsoft.UI.Xaml.GridLength.Auto });

        for (int r = 0; r < dataRows.Count; r++)
        {
            bool isHeader = r == 0;
            for (int c = 0; c < numCols; c++)
            {
                var cellValue = c < dataRows[r].Count ? dataRows[r][c] : "";
                var cellBorder = new Microsoft.UI.Xaml.Controls.Border
                {
                    Background = isHeader
                        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 55, 55, 60))
                        : (r % 2 == 0
                            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 45, 50))
                            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 40, 45))),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 65)),
                    BorderThickness = new Microsoft.UI.Xaml.Thickness(0, 0, 1, 1),
                    Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6),
                    MinWidth = 100,
                };
                cellBorder.Child = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = cellValue,
                    FontSize = 12,
                    FontWeight = isHeader ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 210, 210, 210)),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap,
                };
                Microsoft.UI.Xaml.Controls.Grid.SetRow(cellBorder, r);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        scrollViewer.Content = grid;

        var statusText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            FontSize = 12,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
        };

        var copyMdBtn = new Microsoft.UI.Xaml.Controls.Button { Content = "Copy as Markdown" };
        copyMdBtn.Click += (s, e) =>
        {
            try
            {
                var md = TableExportHelper.ToMarkdown(dataRows);
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(md);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                statusText.Text = "Copied to clipboard.";
            }
            catch (Exception ex)
            {
                statusText.Text = $"Copy failed: {ex.Message}";
            }
        };

        var exportXlsxBtn = new Microsoft.UI.Xaml.Controls.Button { Content = "Export as .xlsx" };
        exportXlsxBtn.Click += async (s, e) =>
        {
            try
            {
                if (App.MainWindow == null)
                {
                    statusText.Text = "Export failed: main window not available.";
                    return;
                }

                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.FileTypeChoices.Add("Excel Workbook", new List<string> { ".xlsx" });
                picker.SuggestedFileName = "table";
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

                // Use reflection to call IInitializeWithWindow.Initialize — avoids
                // CS0030 (local IInitializeWithWindow conflicts with the picker's
                // own implementation), same workaround already used for
                // FolderPicker in SettingsPage.Browse_Click.
                var iidType = picker.GetType().GetInterface("IInitializeWithWindow");
                iidType?.GetMethod("Initialize")?.Invoke(picker, new object[] { hwnd });

                var file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    TableExportHelper.WriteXlsx(file.Path, dataRows);
                    statusText.Text = $"Saved to {file.Path}";
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"Export failed: {ex.Message}";
            }
        };

        var buttonPanel = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0),
        };
        buttonPanel.Children.Add(copyMdBtn);
        buttonPanel.Children.Add(exportXlsxBtn);

        var content = new Microsoft.UI.Xaml.Controls.StackPanel();
        content.Children.Add(scrollViewer);
        content.Children.Add(buttonPanel);
        content.Children.Add(statusText);

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Table",
            Content = content,
            CloseButtonText = "Close",
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
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

        stack.Children.Add(codeBox);

        // Set text + highlight AFTER the box is in the visual tree.
        // SetText() throws UnauthorizedAccessException if the control
        // hasn't been added to a loaded parent yet (no HWND).
        codeBox.Loaded += (s, e) =>
        {
            try
            {
                codeBox.TextDocument.SetText(Microsoft.UI.Text.TextSetOptions.None, code);
                SyntaxHighlighter.ApplyHighlighting(codeBox, language);
            }
            catch { }
        };

        card.Child = stack;

        // Wrap in InlineUIContainer
        var container = new Microsoft.UI.Xaml.Documents.InlineUIContainer { Child = card };
        paragraph.Inlines.Add(container);
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());

        return paragraph;
    }

    /// <summary>
    /// Placeholder card shown while a fenced code block is still streaming in
    /// (opening ``` seen, closing ``` not yet received). Same visual shell as
    /// CreateCodeCard but with a ring progress spinner + "Creating Code" text
    /// instead of the code itself, since there's nothing final to show yet.
    /// </summary>
    private static Microsoft.UI.Xaml.Documents.Paragraph CreatePendingCodeCard(string language)
    {
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();

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

        // Header: language label (matches CreateCodeCard's header so the
        // card doesn't visually "jump" once the real code card replaces it)
        var headerPanel = new Microsoft.UI.Xaml.Controls.Grid
        {
            Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 45, 45, 45)),
        };
        headerPanel.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });

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
        stack.Children.Add(headerPanel);

        // Body: ring progress + "Creating Code" text
        var bodyPanel = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 10,
            Padding = new Microsoft.UI.Xaml.Thickness(14, 14, 14, 14),
        };

        var ring = new Microsoft.UI.Xaml.Controls.ProgressRing
        {
            IsActive = true,
            Width = 18,
            Height = 18,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 160, 160, 160)),
        };
        bodyPanel.Children.Add(ring);

        var statusText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "Creating Code",
            FontSize = 13,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 200, 200, 200)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        bodyPanel.Children.Add(statusText);

        stack.Children.Add(bodyPanel);
        card.Child = stack;

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
