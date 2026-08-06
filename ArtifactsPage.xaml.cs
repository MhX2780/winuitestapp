using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using Newtonsoft.Json;

namespace AdvaBrowser;

public sealed partial class ArtifactsPage : Page
{
    public ArtifactsPage()
    {
        this.InitializeComponent();
        Loaded += ArtifactsPage_Loaded;
    }

    // ── Navigation-aware refresh ──
    // Frame.Navigated fires every time we navigate TO this page,
    // unlike Loaded which only fires the first time the page is created.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.ArtifactsRefreshCallback = RefreshArtifacts;
        RefreshArtifacts();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Unregister callback when leaving so HomePage doesn't hold a stale reference
        if (App.ArtifactsRefreshCallback?.Target == this)
            App.ArtifactsRefreshCallback = null;
    }

    private void ArtifactsPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshArtifacts();
    }

    /// <summary>
    /// Refreshes artifacts from in-memory messages (no disk I/O).
    /// Called from OnNavigatedTo and also from HomePage.OnDone().
    /// </summary>
    public void RefreshArtifacts(List<ChatMessage>? messages = null)
    {
        ArtifactsPanel.Children.Clear();

        var allBlocks = new List<(string Lang, string Code, string Preview, string Source)>();

        if (messages != null)
        {
            // Use in-memory messages directly (fast path)
            int msgIndex = 0;
            foreach (var msg in messages)
            {
                if (msg.Role == "model" && !string.IsNullOrEmpty(msg.Content))
                {
                    var blocks = MarkdownRenderer.ExtractCodeBlocks(msg.Content);
                    foreach (var (lang, code, preview) in blocks)
                        allBlocks.Add((lang, code, preview, $"Message #{msgIndex + 1}"));
                }
                msgIndex++;
            }
        }
        else
        {
            // Fallback: read from disk
            try
            {
                if (System.IO.File.Exists(ConfigManager.ActiveChatFile))
                {
                    var json = System.IO.File.ReadAllText(ConfigManager.ActiveChatFile);
                    var msgs = JsonConvert.DeserializeObject<List<ChatMessage>>(json);
                    if (msgs != null)
                    {
                        int msgIndex = 0;
                        foreach (var msg in msgs)
                        {
                            if (msg.Role == "model" && !string.IsNullOrEmpty(msg.Content))
                            {
                                var blocks = MarkdownRenderer.ExtractCodeBlocks(msg.Content);
                                foreach (var (lang, code, preview) in blocks)
                                    allBlocks.Add((lang, code, preview, $"Message #{msgIndex + 1}"));
                            }
                            msgIndex++;
                        }
                    }
                }
            }
            catch { }
        }

        ArtifactCount.Text = allBlocks.Count > 0 ? $"{allBlocks.Count} artifact(s)" : "";

        if (allBlocks.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;

        for (int i = 0; i < allBlocks.Count; i++)
        {
            var (lang, code, preview, source) = allBlocks[i];
            var card = CreateArtifactCard(i + 1, lang, code, preview, source);
            ArtifactsPanel.Children.Add(card);
        }
    }

    private Border CreateArtifactCard(int number, string language, string code, string preview, string source)
    {
        var card = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 30, 30, 30)),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
            Padding = new Microsoft.UI.Xaml.Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var mainStack = new StackPanel();

        // ── Header ──
        var headerGrid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 45, 45, 45)),
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleLabel = new TextBlock
        {
            Text = $"{source} — {(string.IsNullOrEmpty(language) ? "code" : language.ToUpperInvariant())}",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 180, 180, 180)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleLabel, 0);
        headerGrid.Children.Add(titleLabel);

        var copyBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE8C8", FontSize = 14 },
                    new TextBlock { Text = "Copy", FontSize = 12 },
                }
            },
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 220, 220, 220)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(copyBtn, 1);
        copyBtn.Click += (s, e) =>
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        };
        headerGrid.Children.Add(copyBtn);

        // Expand/Collapse toggle button
        var expandBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE70D", FontSize = 12 },
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 60, 60, 60)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 180, 180, 180)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = "collapsed",
        };
        Grid.SetColumn(expandBtn, 2);
        headerGrid.Children.Add(expandBtn);

        mainStack.Children.Add(headerGrid);

        // ── Separator line between header and code ──
        var separator = new Border
        {
            Height = 1,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 55, 55, 55)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        mainStack.Children.Add(separator);

        // ── Syntax-highlighted code via RichEditBox (scrollable, selectable, full code) ──
        var codeBox = new RichEditBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
            FontSize = 13,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 25, 25, 25)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 212, 212, 212)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 12),
            MinHeight = 60,
            MaxHeight = 400,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        mainStack.Children.Add(codeBox);

        // Apply syntax highlighting AFTER the box is in the visual tree.
        // RichEditBox.TextDocument.SetText() throws UnauthorizedAccessException
        // if the control has no HWND yet (i.e., before it's added to a loaded Panel).
        codeBox.Loaded += (s, e) =>
        {
            try
            {
                codeBox.TextDocument.SetText(Microsoft.UI.Text.TextSetOptions.None, code);
                SyntaxHighlighter.ApplyHighlighting(codeBox, language);
            }
            catch { }
        };

        // ── Expand/Collapse toggle ──
        expandBtn.Click += (s, e) =>
        {
            if (expandBtn.Tag is string state)
            {
                if (state == "collapsed")
                {
                    codeBox.MaxHeight = double.MaxValue; // fully expanded
                    var glyphIcon = (FontIcon)((Button)s).Content;
                    glyphIcon.Glyph = "\uE70E"; // collapse chevron
                    expandBtn.Tag = "expanded";
                }
                else
                {
                    codeBox.MaxHeight = 400;
                    var glyphIcon = (FontIcon)((Button)s).Content;
                    glyphIcon.Glyph = "\uE70D"; // expand chevron
                    expandBtn.Tag = "collapsed";
                }
            }
        };

        card.Child = mainStack;
        return card;
    }
}
