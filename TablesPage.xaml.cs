using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Newtonsoft.Json;

namespace UGA;

/// <summary>
/// Dedicated navigation page listing every table found across the current
/// chat, separate from ArtifactsPage (which is code-only). Mirrors
/// ArtifactsPage's structure: in-memory refresh via App.TablesRefreshCallback
/// while chatting, disk fallback when opened directly.
/// </summary>
public sealed partial class TablesPage : Page
{
    public TablesPage()
    {
        this.InitializeComponent();
        Loaded += TablesPage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.TablesRefreshCallback = RefreshTables;
        RefreshTables();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (App.TablesRefreshCallback?.Target == this)
            App.TablesRefreshCallback = null;
    }

    private void TablesPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshTables();
    }

    /// <summary>
    /// Refreshes the table list from in-memory messages (fast path while
    /// chatting) or from disk (when the page is opened directly / after a
    /// restart). Called from OnNavigatedTo and from HomePage after each
    /// completed response, same pattern as ArtifactsPage.RefreshArtifacts.
    /// </summary>
    public void RefreshTables(List<ChatMessage>? messages = null)
    {
        TablesPanel.Children.Clear();

        var allTables = new List<(List<List<string>> Rows, string Source)>();

        void CollectFrom(IEnumerable<ChatMessage> msgs)
        {
            int msgIndex = 0;
            foreach (var msg in msgs)
            {
                if (msg.Role == "model" && !string.IsNullOrEmpty(msg.Content))
                {
                    var tables = MarkdownRenderer.ExtractTables(msg.Content);
                    foreach (var rows in tables)
                        allTables.Add((rows, $"Message #{msgIndex + 1}"));
                }
                msgIndex++;
            }
        }

        if (messages != null)
        {
            CollectFrom(messages);
        }
        else
        {
            try
            {
                if (System.IO.File.Exists(ConfigManager.ActiveChatFile))
                {
                    var json = System.IO.File.ReadAllText(ConfigManager.ActiveChatFile);
                    var msgs = JsonConvert.DeserializeObject<List<ChatMessage>>(json);
                    if (msgs != null) CollectFrom(msgs);
                }
            }
            catch { }
        }

        TableCount.Text = allTables.Count > 0 ? $"{allTables.Count} table(s)" : "";

        if (allTables.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;

        for (int i = 0; i < allTables.Count; i++)
        {
            var (rows, source) = allTables[i];
            var card = CreateTableListCard(i + 1, rows, source);
            TablesPanel.Children.Add(card);
        }
    }

    private Border CreateTableListCard(int number, List<List<string>> rows, string source)
    {
        var numCols = rows.Max(r => r.Count);
        var numDataRows = System.Math.Max(0, rows.Count - 1);

        var card = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 30, 30, 30)),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var mainStack = new StackPanel();

        // ── Header: source label + row/col summary + action buttons ──
        var headerGrid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 45, 45, 45)),
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Spacing = 2 };
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{source}",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 180, 180, 180)),
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{numDataRows} row{(numDataRows == 1 ? "" : "s")} · {numCols} column{(numCols == 1 ? "" : "s")}",
            FontSize = 11,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 140, 140, 140)),
        });
        Grid.SetColumn(titleStack, 0);
        headerGrid.Children.Add(titleStack);

        // View full table — reuses the same dialog used from the chat's table card
        var viewBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE8A0", FontSize = 14 },
                    new TextBlock { Text = "View", FontSize = 12 },
                }
            },
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 220, 220, 220)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(viewBtn, 1);
        viewBtn.Click += (s, e) => MarkdownRenderer.ShowTableDialog(this.XamlRoot, rows);
        headerGrid.Children.Add(viewBtn);

        var copyBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 },
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 220, 220, 220)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        ToolTipService.SetToolTip(copyBtn, "Copy as Markdown");
        Grid.SetColumn(copyBtn, 2);
        copyBtn.Click += (s, e) =>
        {
            var md = TableExportHelper.ToMarkdown(rows);
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(md);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        };
        headerGrid.Children.Add(copyBtn);

        var exportBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE898", FontSize = 14 }, // save/export glyph
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 220, 220, 220)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        ToolTipService.SetToolTip(exportBtn, "Export as .xlsx");
        Grid.SetColumn(exportBtn, 3);
        exportBtn.Click += async (s, e) =>
        {
            try
            {
                if (App.MainWindow == null) return;

                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.FileTypeChoices.Add("Excel Workbook", new List<string> { ".xlsx" });
                picker.SuggestedFileName = "table";
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                var iidType = picker.GetType().GetInterface("IInitializeWithWindow");
                iidType?.GetMethod("Initialize")?.Invoke(picker, new object[] { hwnd });

                var file = await picker.PickSaveFileAsync();
                if (file != null)
                    TableExportHelper.WriteXlsx(file.Path, rows);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("ERROR", $"TablesPage export failed: {ex.Message}");
            }
        };
        headerGrid.Children.Add(exportBtn);

        mainStack.Children.Add(headerGrid);

        // ── Separator ──
        mainStack.Children.Add(new Border
        {
            Height = 1,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 55, 55, 55)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        // ── Compact preview: header row + up to 3 data rows ──
        var previewRows = rows.Take(4).ToList();
        var previewGrid = new Grid { Padding = new Thickness(12, 8, 12, 8) };
        for (int c = 0; c < numCols; c++)
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < previewRows.Count; r++)
            previewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < previewRows.Count; r++)
        {
            bool isHeader = r == 0;
            for (int c = 0; c < numCols; c++)
            {
                var cellValue = c < previewRows[r].Count ? previewRows[r][c] : "";
                var cellText = new TextBlock
                {
                    Text = cellValue,
                    FontSize = 12,
                    FontWeight = isHeader ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 200, 200, 200)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 12, 2),
                };
                Grid.SetRow(cellText, r);
                Grid.SetColumn(cellText, c);
                previewGrid.Children.Add(cellText);
            }
        }
        mainStack.Children.Add(previewGrid);

        if (rows.Count > 4)
        {
            mainStack.Children.Add(new TextBlock
            {
                Text = $"+ {rows.Count - 4} more row(s) — tap View for the full table",
                FontSize = 11,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 140, 140, 140)),
                Padding = new Thickness(12, 0, 12, 10),
            });
        }
        else
        {
            mainStack.Children.Add(new Border { Height = 6 });
        }

        card.Child = mainStack;
        return card;
    }
}
