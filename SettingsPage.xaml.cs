using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using System.Runtime.InteropServices;

namespace AdvaBrowser;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();
        LoadUI();
    }

    private void LoadUI()
    {
        try
        {
            var s = ConfigManager.Settings;
            ApiKeyBox.Password = ConfigManager.LoadSavedApiKey();
            KeyStatus.Text = string.IsNullOrEmpty(ApiKeyBox.Password) ? "No API key set." : "API key saved.";

            // Set model combo
            for (int i = 0; i < ModelCombo.Items.Count; i++)
            {
                if (((ComboBoxItem)ModelCombo.Items[i]).Content?.ToString()
                    == (ConfigManager.ModelChain.FirstOrDefault()?.Name ?? ""))
                {
                    ModelCombo.SelectedIndex = i;
                    break;
                }
            }

            ModelChainBox.Text = string.Join(", ", ConfigManager.ModelChain.Select(m => m.Name));
            WorkspaceBox.Text = s.WorkspacePath;
            UpdateWsStatus();

            // Show log path
            LogPathText.Text = $"Logs: {CrashLogger.LogFilePath}";

            // Providers
            ClaudeKeyBox.Password = ConfigManager.LoadProviderApiKey("claude");
            OpenAIKeyBox.Password = ConfigManager.LoadProviderApiKey("openai");
            PuterTokenBox.Password = ConfigManager.LoadPuterToken();

            // Memory
            SystemPromptBox.Text = s.SystemPromptOverride;
            MaxHistory.Value = s.MaxHistoryMessages;

            // Deep Thinking
            DeepThinking.IsOn = s.DeepThinkingEnabled;
            ThinkBudget.Value = s.DeepThinkingBudget;
            ThinkInclude.IsOn = s.DeepThinkingIncludeThoughts;

            // Multi-Agent
            MultiAgent.IsOn = s.MultiAgentEnabled;
            SetCombo(ClassifierCombo, s.MultiAgentRoles?.GetValueOrDefault("classifier") ?? "gemini-2.5-flash-lite");
            SetCombo(PlannerCombo, s.MultiAgentRoles?.GetValueOrDefault("planner") ?? "gemini-3.6-flash");
            SetCombo(ExecutorCombo, s.MultiAgentRoles?.GetValueOrDefault("executor") ?? "gemini-3.5-flash");
            SetCombo(ReviewerCombo, s.MultiAgentRoles?.GetValueOrDefault("reviewer") ?? "gemini-2.5-flash-lite");

            // Theme
            SetThemeCombo(s.ThemeMode);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"LoadUI failed: {ex.Message}");
        }
    }

    private void UpdateWsStatus()
    {
        var p = WorkspaceBox.Text?.Trim();
        if (string.IsNullOrEmpty(p))
            WorkspaceStatus.Text = $"Default: {ConfigManager.WorkspaceDir}";
        else if (Directory.Exists(p))
            WorkspaceStatus.Text = "Directory exists.";
        else
            WorkspaceStatus.Text = "Will be created on first use.";
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        CrashLogger.Log("INFO", "Browse_Click: Starting folder picker");

        string? selectedPath = null;

        // Strategy 1: WinRT FolderPicker
        try
        {
            var mainWindow = App.MainWindow;
            if (mainWindow == null)
            {
                CrashLogger.Log("ERROR", "Browse_Click: App.MainWindow is null!");
                WorkspaceStatus.Text = "Error: Main window not available.";
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
            CrashLogger.Log("INFO", $"Browse_Click: hwnd=0x{hwnd.ToInt64():X16}");

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            CrashLogger.Log("INFO", "Browse_Click: FolderPicker created, initializing window");

            // Use reflection to call IInitializeWithWindow.Initialize to avoid
            // CS0030: local IInitializeWithWindow conflicts with FolderPicker's implementation
            try
            {
                var iidType = picker.GetType().GetInterface("IInitializeWithWindow");
                if (iidType != null)
                {
                    iidType.GetMethod("Initialize")?.Invoke(picker, new object[] { hwnd });
                    CrashLogger.Log("INFO", "IInitializeWithWindow.Initialize called via reflection");
                }
                else
                {
                    CrashLogger.Log("WARN", "IInitializeWithWindow not found on FolderPicker, trying direct Win32 fallback");
                    selectedPath = BrowseForFolderWin32();
                    WorkspaceBox.Text = selectedPath ?? "";
                    UpdateWsStatus();
                    return;
                }
            }
            catch (Exception initEx)
            {
                CrashLogger.Log("WARN", $"IInitializeWithWindow failed: {initEx.Message}, trying Win32 fallback");
                selectedPath = BrowseForFolderWin32();
                WorkspaceBox.Text = selectedPath ?? "";
                UpdateWsStatus();
                return;
            }

            CrashLogger.Log("INFO", "Browse_Click: Calling PickSingleFolderAsync");
            var folder = await picker.PickSingleFolderAsync();
            CrashLogger.Log("INFO", $"Browse_Click: FolderPicker result={folder?.Path ?? "null"}");

            if (folder != null)
            {
                selectedPath = folder.Path;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"Browse_Click: FolderPicker failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            CrashLogger.WriteCrash($"FolderPicker failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

            // Strategy 2: Win32 SHBrowseForFolder fallback
            CrashLogger.Log("INFO", "Browse_Click: Trying Win32 SHBrowseForFolder fallback");
            try
            {
                selectedPath = BrowseForFolderWin32();
                CrashLogger.Log("INFO", $"Browse_Click: Win32 result={selectedPath ?? "null"}");
            }
            catch (Exception ex2)
            {
                CrashLogger.Log("ERROR", $"Browse_Click: Win32 fallback also failed: {ex2.Message}");
                WorkspaceStatus.Text = $"Folder picker error: {ex.Message}. Win32 fallback: {ex2.Message}";
                return;
            }
        }

        if (!string.IsNullOrEmpty(selectedPath))
        {
            WorkspaceBox.Text = selectedPath;
            UpdateWsStatus();
        }
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var k = ApiKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(k)) { KeyStatus.Text = "Enter a valid key."; return; }
        ConfigManager.SaveApiKey(k);
        KeyStatus.Text = "Key saved.";
        CrashLogger.Log("INFO", "API key saved");
    }

    private void ResetKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = "";
        ConfigManager.DeleteApiKey();
        KeyStatus.Text = "Key removed.";
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var s = ConfigManager.Settings;

        // Model chain
        var modelName = ((ComboBoxItem)ModelCombo.SelectedItem)?.Content?.ToString()
            ?? "gemini-2.5-flash";
        var chainNames = ModelChainBox.Text?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? new[] { modelName };
        s.ModelChain = chainNames.Select(n => new ModelChainEntry { Name = n }).ToList();

        // Workspace
        s.WorkspacePath = WorkspaceBox.Text?.Trim() ?? "";

        // Theme
        var themeTag = ((ComboBoxItem)ThemeCombo.SelectedItem)?.Tag?.ToString() ?? "auto";
        s.ThemeMode = themeTag;
        ApplyThemeImmediate(themeTag);

        // Providers
        ConfigManager.SaveProviderApiKey("claude", ClaudeKeyBox.Password?.Trim() ?? "");
        ConfigManager.SaveProviderApiKey("openai", OpenAIKeyBox.Password?.Trim() ?? "");
        if (!string.IsNullOrEmpty(PuterTokenBox.Password?.Trim()))
            ConfigManager.SavePuterToken(PuterTokenBox.Password.Trim());

        // Multi-Agent
        s.MultiAgentEnabled = MultiAgent.IsOn;
        s.MultiAgentRoles = new()
        {
            ["classifier"] = ((ComboBoxItem)ClassifierCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-2.5-flash-lite",
            ["planner"] = ((ComboBoxItem)PlannerCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-3.6-flash",
            ["executor"] = ((ComboBoxItem)ExecutorCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-3.5-flash",
            ["reviewer"] = ((ComboBoxItem)ReviewerCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-2.5-flash-lite",
        };

        // Deep Thinking
        s.DeepThinkingEnabled = DeepThinking.IsOn;
        s.DeepThinkingBudget = (int)ThinkBudget.Value;
        s.DeepThinkingIncludeThoughts = ThinkInclude.IsOn;

        // Memory
        s.SystemPromptOverride = SystemPromptBox.Text?.Trim() ?? "";
        s.MaxHistoryMessages = (int)MaxHistory.Value;

        ConfigManager.SaveSettings();
        SaveStatus.Text = "All settings saved!";
        CrashLogger.Log("INFO", "All settings saved");

        // Clear status after 3s
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            DispatcherQueue.TryEnqueue(() => SaveStatus.Text = "");
        });
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        ConfigManager.Settings = new();
        ConfigManager.SaveSettings();
        LoadUI();
        SaveStatus.Text = "Reset to defaults.";
    }

    private async void ShowLogs_Click(object sender, RoutedEventArgs e)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UGA");
        var logContent = $"=== APP LOG ===\n{CrashLogger.ReadAppLog()}\n\n=== CRASH LOG ===\n{CrashLogger.ReadCrashLog()}";

        var dlg = new ContentDialog
        {
            Title = "Application Logs",
            PrimaryButtonText = "Open Log Folder",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };

        // Show truncated log in dialog
        var maxLen = 3000;
        var display = logContent.Length > maxLen
            ? logContent.Substring(logContent.Length - maxLen) + "\n... (truncated)"
            : logContent;

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400,
            Padding = new Thickness(0, 0, 0, 8),
        };
        scrollViewer.Content = new TextBlock
        {
            Text = display,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
        };
        dlg.Content = scrollViewer;

        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // Open log folder in Explorer
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", logDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogPathText.Text = $"Could not open folder: {ex.Message}";
            }
        }
    }

    private static void SetCombo(ComboBox combo, string text)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (((ComboBoxItem)combo.Items[i]).Content?.ToString() == text)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    // ─── Theme ───

    private void SetThemeCombo(string mode)
    {
        for (int i = 0; i < ThemeCombo.Items.Count; i++)
        {
            if (((ComboBoxItem)ThemeCombo.Items[i]).Tag?.ToString() == mode)
            {
                ThemeCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "auto";
        ThemeStatus.Text = tag switch
        {
            "auto" => "Follows Windows system theme setting.",
            "dark" => "Always use dark theme.",
            "light" => "Always use light theme.",
            _ => ""
        };
        // Live preview
        ApplyThemeImmediate(tag);
    }

    private void ApplyThemeImmediate(string mode)
    {
        if (App.MainWindow is MainWindow mw)
            mw.ApplyTheme(mode);
    }

    // ─── Win32 SHBrowseForFolder fallback ───

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);

    private class BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private static string? BrowseForFolderWin32()
    {
        var mainWindow = App.MainWindow;
        if (mainWindow == null) return null;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);

        var bi = new BROWSEINFO
        {
            hwndOwner = hwnd,
            lpszTitle = "Select Workspace Directory",
            ulFlags = 0x0041 // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
        };

        var pidl = SHBrowseForFolder(ref bi);
        if (pidl == IntPtr.Zero) return null;

        var sb = new StringBuilder(260);
        var result = SHGetPathFromIDList(pidl, sb);
        CoTaskMemFree(pidl);

        return result ? sb.ToString() : null;
    }
}
