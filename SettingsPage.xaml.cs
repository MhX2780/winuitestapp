using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using System.Runtime.InteropServices;

namespace UGA;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();
        this.Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Load UI immediately (sync) so user sees settings right away
            LoadUI();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"SettingsPage_Loaded (LoadUI) failed: {ex.Message}");
        }

        // Hide spinner, show content — Puter models load in background
        LoadingOverlay.Visibility = Visibility.Collapsed;
        SettingsScroll.Visibility = Visibility.Visible;

        // Load Puter models NON-BLOCKING — runs in background after page is visible
        try
        {
            await LoadModelsAsyncWrapped();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"SettingsPage_Loaded (LoadModels) failed: {ex.Message}");
        }
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

            ModelChainBreadcrumb.ItemsSource = ConfigManager.ModelChain.Select(m => m.Name).ToArray();
            WorkspaceBox.Text = s.WorkspacePath;
            UpdateWsStatus();

            // Show log path
            LogPathText.Text = $"Logs: {CrashLogger.LogFilePath}";

            // Providers
            ClaudeKeyBox.Password = ConfigManager.LoadProviderApiKey("claude");
            OpenAIKeyBox.Password = ConfigManager.LoadProviderApiKey("openai");
            PuterTokenBox.Password = ConfigManager.LoadPuterToken();

            // Puter.js settings
            PuterChat.IsOn = s.PuterChatEnabled;
            PuterFreeOnly.IsOn = s.PuterFreeOnly;
            PuterToolCalling.IsOn = s.PuterToolCallingEnabled;
            PuterImageTools.IsOn = s.PuterImageToolsEnabled;
            PuterDeepThinking.IsOn = s.PuterDeepThinkingEnabled;
            // Puter model boxes are now ComboBoxes - populated by LoadModelsAsync
            SetCombo(PuterEffortCombo, s.PuterDeepThinkingEffort);

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

            _isLoadingUI = false;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"LoadUI failed: {ex.Message}");
            _isLoadingUI = false;
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

                // Strategy 3: Ask user to paste path via ContentDialog
                CrashLogger.Log("INFO", "Browse_Click: Trying ContentDialog text input fallback");
                try
                {
                    var inputBox = new TextBox
                    {
                        PlaceholderText = "Paste workspace directory path...",
                        Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        Width = 400,
                    };
                    var inputPanel = new StackPanel { Spacing = 8 };
                    inputPanel.Children.Add(new TextBlock
                    {
                        Text = $"Both folder pickers failed. Please paste the directory path manually:",
                        TextWrapping = TextWrapping.Wrap,
                    });
                    inputPanel.Children.Add(inputBox);

                    var inputDlg = new ContentDialog
                    {
                        Title = "Enter Workspace Path",
                        Content = inputPanel,
                        PrimaryButtonText = "Use This Path",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = XamlRoot,
                    };
                    if (await inputDlg.ShowAsync() == ContentDialogResult.Primary)
                    {
                        selectedPath = inputBox.Text?.Trim();
                    }
                }
                catch (Exception ex3)
                {
                    CrashLogger.Log("ERROR", $"Browse_Click: ContentDialog fallback failed: {ex3.Message}");
                }

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

        // Model chain - already updated via AddModelBtn/RemoveLastModelBtn and BreadcrumbBar\n        // Just persist current chain from ConfigManager\n        var chainNames = ConfigManager.ModelChain.Select(m => m.Name).ToList();

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

        // Puter.js settings
        s.PuterChatEnabled = PuterChat.IsOn;
        s.PuterFreeOnly = PuterFreeOnly.IsOn;
        s.PuterToolCallingEnabled = PuterToolCalling.IsOn;
        s.PuterImageToolsEnabled = PuterImageTools.IsOn;
        s.PuterDeepThinkingEnabled = PuterDeepThinking.IsOn;
        s.PuterFreeChatModel = ((ComboBoxItem)PuterModelBox.SelectedItem)?.Content?.ToString()
            ?? s.PuterFreeChatModel;
        s.PuterVisionModel = ((ComboBoxItem)PuterVisionBox.SelectedItem)?.Content?.ToString()
            ?? s.PuterVisionModel;
        s.PuterImageGenModel = ((ComboBoxItem)PuterImageGenBox.SelectedItem)?.Content?.ToString()
            ?? s.PuterImageGenModel;
        s.PuterDeepThinkingEffort = ((ComboBoxItem)PuterEffortCombo.SelectedItem)?.Tag?.ToString() ?? "high";

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

    /// <summary>
    /// Wrapper that returns Task so we can await it from SettingsPage_Loaded.
    /// </summary>
    private async System.Threading.Tasks.Task LoadModelsAsyncWrapped()
    {
        await LoadModelsAsyncCore();
    }

    private async void LoadModelsAsync()
    {
        await LoadModelsAsyncCore();
    }

    private async System.Threading.Tasks.Task LoadModelsAsyncCore()
    {
        // Load Gemini models from default chain
        var geminiModels = ConfigManager.DefaultModelChain.Select(m => m.Name).ToList();

        foreach (var combo in new[] { ModelCombo, ClassifierCombo, PlannerCombo, ExecutorCombo, ReviewerCombo, AddModelCombo })
        {
            combo.Items.Clear();
            foreach (var model in geminiModels)
                combo.Items.Add(new ComboBoxItem { Content = model });
        }

        // Select previously chosen models
        SetCombo(ModelCombo, ConfigManager.ModelChain.FirstOrDefault()?.Name ?? geminiModels[0]);
        SetCombo(ClassifierCombo, ConfigManager.MultiAgentRoles?.GetValueOrDefault("classifier") ?? "gemini-2.5-flash-lite");
        SetCombo(PlannerCombo, ConfigManager.MultiAgentRoles?.GetValueOrDefault("planner") ?? "gemini-3.6-flash");
        SetCombo(ExecutorCombo, ConfigManager.MultiAgentRoles?.GetValueOrDefault("executor") ?? "gemini-3.5-flash");
        SetCombo(ReviewerCombo, ConfigManager.MultiAgentRoles?.GetValueOrDefault("reviewer") ?? "gemini-2.5-flash-lite");

        // Populate BreadcrumbBar
        UpdateBreadcrumbBar();

        // Load Puter models if token exists
        var token = ConfigManager.LoadPuterToken();
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                using var puter = new PuterService();
                List<string> puterModels;
                try
                {
                    puterModels = await puter.ListModelsAsync();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("WARN", $"ListModelsAsync failed: {ex.Message}");
                    PuterModelsStatus.Text = "Could not fetch Puter models. Try again later.";
                    return;
                }
                PuterModelsStatus.Text = $"Puter: {puterModels.Count} models loaded.";

                // Add Puter models to ALL model ComboBoxes (Gemini + Puter combined)
                foreach (var combo in new[] { ModelCombo, ClassifierCombo, PlannerCombo, ExecutorCombo, ReviewerCombo, AddModelCombo })
                {
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = "── Puter.js Models ──",
                        IsEnabled = false,
                    });
                    foreach (var model in puterModels)
                        combo.Items.Add(new ComboBoxItem { Content = model });
                }

                // Populate Puter-specific comboboxes (chat, vision, image gen)
                var chatModels = puterModels.ToList();
                foreach (var m in chatModels)
                    PuterModelBox.Items.Add(new ComboBoxItem { Content = m });
                SetCombo(PuterModelBox, ConfigManager.Settings.PuterFreeChatModel);

                // Vision models: filter by known vision keywords or show all
                var visionKeywords = new[] { "vision", "vlm", "multimodal", "gpt-4o", "claude-3", "gemini" };
                var visionModels = puterModels.Where(m =>
                    visionKeywords.Any(k => m.ToLowerInvariant().Contains(k.ToLowerInvariant()))).ToList();
                if (visionModels.Count == 0) visionModels = puterModels; // fallback: show all
                foreach (var m in visionModels)
                    PuterVisionBox.Items.Add(new ComboBoxItem { Content = m });
                SetCombo(PuterVisionBox, ConfigManager.Settings.PuterVisionModel);

                // Image gen models: filter by known keywords
                var imgKeywords = new[] { "dall-e", "image", "img", "flux", "stable", "sd", "paint", "draw" };
                var imgModels = puterModels.Where(m =>
                    imgKeywords.Any(k => m.ToLowerInvariant().Contains(k.ToLowerInvariant()))).ToList();
                if (imgModels.Count == 0) imgModels = puterModels; // fallback: show all
                foreach (var m in imgModels)
                    PuterImageGenBox.Items.Add(new ComboBoxItem { Content = m });
                SetCombo(PuterImageGenBox, ConfigManager.Settings.PuterImageGenModel);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("WARN", $"Puter models section failed: {ex.Message}");
                PuterModelsStatus.Text = "Puter token set but couldn't fetch models. Will retry on demand.";
            }
        }
    }

    private void UpdateBreadcrumbBar()
    {
        var chain = ConfigManager.ModelChain;
        var items = new List<Microsoft.UI.Xaml.Controls.BreadcrumbBarItem>();
        foreach (var entry in chain)
        {
            items.Add(new Microsoft.UI.Xaml.Controls.BreadcrumbBarItem
            {
                Content = entry.Name,
                Tag = entry,
            });
        }
        ModelChainBreadcrumb.ItemsSource = items;
    }

    private void AddModelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AddModelCombo.SelectedItem is ComboBoxItem item && !string.IsNullOrEmpty(item.Content?.ToString()))
        {
            var name = item.Content.ToString()!;
            if (name == "── Puter.js Models ──") return;

            var chain = ConfigManager.ModelChain.ToList();
            // Avoid duplicates
            if (chain.Any(m => m.Name == name)) return;
            chain.Add(new ModelChainEntry { Name = name });
            ConfigManager.Settings.ModelChain = chain;
            UpdateBreadcrumbBar();
            CrashLogger.Log("INFO", $"Added model to chain: {name}");
        }
    }

    private void RemoveLastModelBtn_Click(object sender, RoutedEventArgs e)
    {
        var chain = ConfigManager.ModelChain.ToList();
        if (chain.Count > 1)
        {
            chain.RemoveAt(chain.Count - 1);
            ConfigManager.Settings.ModelChain = chain;
            UpdateBreadcrumbBar();
            CrashLogger.Log("INFO", "Removed last model from chain");
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

    private bool _isLoadingUI = true;

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "auto";
        // Guard: ThemeStatus may be null during InitializeComponent if SelectedIndex="0"
        // fires SelectionChanged before all named XAML fields are wired up.
        if (ThemeStatus != null)
        {
            ThemeStatus.Text = tag switch
            {
                "auto" => "Follows Windows system theme setting.",
                "dark" => "Always use dark theme.",
                "light" => "Always use light theme.",
                _ => ""
            };
        }
        // Live preview (skip during initial LoadUI to avoid redundant ApplyTheme)
        if (!_isLoadingUI)
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

    // ─── Puter.js model listing ───

    private async void ListPuterModels_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ConfigManager.LoadPuterToken()))
        {
            PuterModelsStatus.Text = "No Puter token configured.";
            return;
        }
        ListPuterModelsButton.IsEnabled = false;
        PuterModelsStatus.Text = "Fetching models...";
        try
        {
            using var puter = new PuterService();
            var models = await puter.ListModelsAsync();
            PuterModelsStatus.Text = $"Found {models.Count} models. First 20: {string.Join(", ", models.Take(20))}";
        }
        catch (Exception ex)
        {
            PuterModelsStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ListPuterModelsButton.IsEnabled = true;
        }
    }

    private async void ListFreeModels_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ConfigManager.LoadPuterToken()))
        {
            PuterModelsStatus.Text = "No Puter token configured.";
            return;
        }
        ListFreeModelsButton.IsEnabled = false;
        PuterModelsStatus.Text = "Fetching free models...";
        try
        {
            using var puter = new PuterService();
            var models = await puter.ListFreeModelsAsync();
            PuterModelsStatus.Text = $"Found {models.Count} free models: {string.Join(", ", models.Take(20))}";
        }
        catch (Exception ex)
        {
            PuterModelsStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ListFreeModelsButton.IsEnabled = true;
        }
    }
}
