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

        // Extended title bar
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        // Caption button colors for acrylic backdrop
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
        AppWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(80, 255, 255, 255);
        AppWindow.TitleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 255, 255, 255);

        // Window size
        this.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, 1200, 800));
        this.AppWindow.Title = "UGA";

        // Taskbar progress
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        TaskbarProgress.Initialize(hwnd);

        // Custom pane header as drag region
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
        RootNav.IsPaneOpen = false;
        RootNav.SelectedItem = RootNav.MenuItems[0];
        Navigate("home");
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            if (tag is "undo" or "clear" or "newchat")
            {
                _ = HandleNavActionAsync(tag);
                RootNav.SelectedItem = RootNav.MenuItems[0];
                return;
            }
            Navigate(tag);
        }
    }

    private async System.Threading.Tasks.Task HandleNavActionAsync(string action)
    {
        RootNav.IsPaneOpen = false;

        if (ContentFrame.Content is not HomePage page) return;

        var (title, message) = action switch
        {
            "undo" => ("Undo Last Change", "Are you sure you want to undo the last file change made by the agent?"),
            "clear" => ("Clear Chat History", "Are you sure you want to clear all chat messages? This cannot be undone."),
            "newchat" => ("New Conversation", "Start a new conversation? Current chat history will be cleared."),
            _ => ("", "")
        };

        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            },
            PrimaryButtonText = "Confirm",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ContentFrame.XamlRoot,
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            switch (action)
            {
                case "undo": page.ExecuteUndo(); break;
                case "clear": page.ExecuteClearChat(); break;
                case "newchat": page.ExecuteNewChat(); break;
            }
        }
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
