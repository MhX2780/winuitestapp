using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdvaBrowser;

public sealed partial class MainWindow : Window
{
    private const int WINDOW_WIDTH = 1200;
    private const int WINDOW_HEIGHT = 800;

    public MainWindow()
    {
        this.InitializeComponent();

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        this.AppWindow.Title = "UGA";

        // Start maximized
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            this.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                displayArea.WorkArea.X,
                displayArea.WorkArea.Y,
                displayArea.WorkArea.Width,
                displayArea.WorkArea.Height));
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        TaskbarProgress.Initialize(hwnd);

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

        // Apply theme based on saved setting
        ApplyTheme(ConfigManager.Settings.ThemeMode);

        // Listen for system theme changes when in Auto mode
        Microsoft.UI.ViewManagement.UISettings uiSettings = new();
        uiSettings.ColorValuesChanged += (s, e) =>
        {
            if (ConfigManager.Settings.ThemeMode == "auto")
            {
                DispatcherQueue.TryEnqueue(() => ApplyTheme("auto"));
            }
        };
    }

    /// <summary>
    /// Applies theme: "auto" = follow system, "dark", "light".
    /// </summary>
    public void ApplyTheme(string mode)
    {
        bool isDark;
        if (mode == "auto")
        {
            // Detect Windows system theme via registry or UISettings
            isDark = IsSystemDark();
        }
        else
        {
            isDark = mode == "dark";
        }

        RootNav.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        UpdateCaptionColors(isDark);
    }

    private static bool IsSystemDark()
    {
        try
        {
            // Read Windows personalization theme from registry
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var val = key.GetValue("AppsUseLightTheme");
                if (val is int v)
                    return v == 0; // 0 = dark, 1 = light
            }
        }
        catch { }
        return true; // Default dark
    }

    private void UpdateCaptionColors(bool dark)
    {
        if (dark)
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 255, 255, 255);
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
            AppWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(80, 255, 255, 255);
            AppWindow.TitleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }
        else
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 0, 0, 0);
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 0, 0, 0);
            AppWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(80, 0, 0, 0);
            AppWindow.TitleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
        }
    }

    /// <summary>
    /// Shows loading screen first, then navigates to chat after init completes.
    /// </summary>
    public void NavigateToLoading()
    {
        LoadingPage.LoadingComplete -= OnLoadingComplete;
        LoadingPage.LoadingComplete += OnLoadingComplete;
        ContentFrame.Navigate(typeof(LoadingPage));
    }

    private void OnLoadingComplete(object? sender, EventArgs e)
    {
        LoadingPage.LoadingComplete -= OnLoadingComplete;
        DispatcherQueue.TryEnqueue(() =>
        {
            ContentFrame.Navigate(typeof(HomePage));
        });
    }

    private void RootNav_Loaded(object sender, RoutedEventArgs e)
    {
        RootNav.IsPaneOpen = false;
        RootNav.SelectedItem = RootNav.MenuItems[0];
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            if (tag is "undo" or "clear" or "newchat" or "history")
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

        if (action == "history")
        {
            if (ContentFrame.Content is HomePage hp)
                hp.ShowChatHistory();
            return;
        }

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
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
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
