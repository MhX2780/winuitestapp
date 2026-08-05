using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AdvaBrowser;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        var appWindow = this.AppWindow;
        if (appWindow != null)
        {
            appWindow.Resize(new SizeInt32(1200, 800));
        }
    }

    private void RootNav_Loaded(object sender, RoutedEventArgs e)
    {
        // Select the first item by default
        if (RootNav.MenuItems.Count > 0)
        {
            RootNav.SelectedItem = RootNav.MenuItems[0];
        }
        NavigateTo("home");
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        // Placeholder navigation. Wire up real pages here as they are built.
        ShowLoading(true);

        ContentFrame.Content = new TextBlock
        {
            Text = $"Section: {tag}",
            FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        ShowLoading(false);
    }

    private void ShowLoading(bool isLoading)
    {
        LoadingRing.IsActive = isLoading;
        LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }
}
