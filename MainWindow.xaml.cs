using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdvaBrowser;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        // Extended title bar (from Content.md)
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        // Set window size
        this.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, 1200, 800));
        this.AppWindow.Title = "UGA";

        // Initialize taskbar progress
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        TaskbarProgress.Initialize(hwnd);

        // Custom pane header as drag region for title bar
        RootNav.PaneHeader = new Grid
        {
            Height = 48,
            Children =
            {
                new TextBlock
                {
                    Text = "UGA",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 0, 0),
                }
            }
        };
    }

    private void RootNav_Loaded(object sender, RoutedEventArgs e)
    {
        RootNav.SelectedItem = RootNav.MenuItems[0];
        Navigate("home");
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }

    private void Navigate(string tag)
    {
        ContentFrame.Navigate(tag switch
        {
            "home" => typeof(HomePage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        });
    }
}
