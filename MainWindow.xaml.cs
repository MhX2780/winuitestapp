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

        // Style caption buttons for acrylic backdrop
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
        AppWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(80, 255, 255, 255);
        AppWindow.TitleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 255, 255, 255);

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
        SetupNavCursors();
    }

    private void SetupNavCursors()
    {
        // Hand cursor on all NavigationView menu items
        foreach (NavigationViewItem item in RootNav.MenuItems)
            CursorHelper.SetHandOn(item);
        foreach (NavigationViewItem item in RootNav.FooterMenuItems)
            CursorHelper.SetHandOn(item);
        // Pane toggle button (hamburger) - find via VisualTreeHelper
        foreach (var child in FindVisualChildren<Button>(RootNav))
            CursorHelper.SetHandOn(child);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var grandchild in FindVisualChildren<T>(child))
                yield return grandchild;
        }
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
