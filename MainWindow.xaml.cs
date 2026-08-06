using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace AdvaBrowser;

public sealed partial class MainWindow : Window
{
    private const int WINDOW_WIDTH = 1200;
    private const int WINDOW_HEIGHT = 800;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    private const uint WM_SETICON = 0x0080;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    public MainWindow()
    {
        this.InitializeComponent();

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        this.AppWindow.Title = "UGA";

        // Start maximized using DisplayArea
        try
        {
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    displayArea.WorkArea.X,
                    displayArea.WorkArea.Y,
                    displayArea.WorkArea.Width,
                    displayArea.WorkArea.Height));
            }
        }
        catch { /* non-critical, window will show at default size */ }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        TaskbarProgress.Initialize(hwnd);

        // Set window icon from app.ico
        SetWindowIcon(hwnd);

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
        Windows.UI.ViewManagement.UISettings uiSettings = new();
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, uint lpReserved, out uint lpType, byte[] lpData, ref int lpcbData);

    private const uint HKEY_CURRENT_USER = 1;
    private const uint KEY_READ = 0x20019;
    private const uint REG_DWORD = 4;

    private static bool IsSystemDark()
    {
        try
        {
            IntPtr hKey;
            if (RegOpenKeyExW((IntPtr)HKEY_CURRENT_USER,
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                0, KEY_READ, out hKey) == 0)
            {
                int size = 4;
                uint type;
                var data = new byte[4];
                if (RegQueryValueExW(hKey, "AppsUseLightTheme", 0, out type, data, ref size) == 0 && type == REG_DWORD)
                {
                    int val = BitConverter.ToInt32(data, 0);
                    RegCloseKey(hKey);
                    return val == 0; // 0 = dark, 1 = light
                }
                RegCloseKey(hKey);
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
    /// Sets the window icon (taskbar + title bar) from app.ico next to the EXE.
    /// </summary>
    private static void SetWindowIcon(IntPtr hwnd)
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var icoPath = Path.Combine(exeDir, "app.ico");
            if (!File.Exists(icoPath)) return;

            IntPtr hIconBig = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            if (hIconBig != IntPtr.Zero)
            {
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIconBig);
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconBig);
            }
        }
        catch { /* non-critical */ }
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
            if (ContentFrame.Content is not HomePage)
            {
                // Navigate to chat first, then show history after a short delay
                ContentFrame.Navigate(typeof(HomePage));
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ContentFrame.Content is HomePage hp)
                            hp.ShowChatHistory();
                    });
                });
                return;
            }
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
        var targetType = tag switch
        {
            "home" => typeof(HomePage),
            "artifacts" => typeof(ArtifactsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };

        // If already showing this page, don't re-navigate
        if (ContentFrame.Content?.GetType() == targetType) return;

        ContentFrame.Navigate(targetType);
    }
}
