using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Newtonsoft.Json;

namespace AdvaBrowser;

public sealed partial class ArtifactsPage : Page
{
    public ArtifactsPage()
    {
        this.InitializeComponent();
        Loaded += ArtifactsPage_Loaded;
    }

    private void ArtifactsPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshArtifacts();
    }

    public void RefreshArtifacts()
    {
        ArtifactsPanel.Children.Clear();

        // Collect all code blocks from all model messages in current chat
        var allBlocks = new List<(string Lang, string Code, string Preview, string Source)>();

        // Read from active chat file
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
                            {
                                allBlocks.Add((lang, code, preview, $"Message #{msgIndex + 1}"));
                            }
                        }
                        msgIndex++;
                    }
                }
            }
        }
        catch { }

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

        // Header
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
            Text = $"{source} — {language.ToUpperInvariant()}",
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

        mainStack.Children.Add(headerGrid);

        // Preview line
        if (!string.IsNullOrEmpty(preview))
        {
            var previewBlock = new TextBlock
            {
                Text = preview,
                FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 140, 140, 140)),
                Padding = new Thickness(12, 4, 12, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };
            mainStack.Children.Add(previewBlock);
        }

        // Code content (collapsible)
        var codeText = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
            FontSize = 12,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 25, 25, 25)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 200, 200, 200)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 12),
            MaxHeight = 300,
            Margin = new Thickness(0),
        };
        mainStack.Children.Add(codeText);

        card.Child = mainStack;
        return card;
    }
}
